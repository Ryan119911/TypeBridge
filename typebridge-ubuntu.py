#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TypeBridge Ubuntu 端守护进程（单线程 GLib 事件循环版）
接收 Windows 端通过 TCP 推送的文本/按键，注入到本机当前焦点窗口。

线路协议（UTF-8，按行分隔，\n 结尾）:
  T <json字符串>   注入文本（json 编码，可含 \n，会转成回车键）
  K <键名>          注入按键: BackSpace Return Tab Escape Delete Home End
                    Up Down Left Right Prior Next 及 ctrl/alt/shift/super 组合
  M <dx> <dy>       相对移动指针（像素，可为负）
  B <按钮> down|up  按下/松开鼠标按钮（1 左 2 中 3 右 8 后退 9 前进）
  W up|down|left|right  滚轮
  PING              应答 PONG
  QUIT              断开
  C <json字符串>    写本机剪贴板（文本，Windows -> Ubuntu）
  BT                通知 Windows 端"切回本机"（Ubuntu 托盘菜单用，Ubuntu -> Windows）

文件通道（默认 4322/4323，独立连接，一行头 + 原始字节）:
  UPLOAD <字节数> <文件名>   收文件并落盘
  CLIPIMG <图片字节数> <文本字节数>  收图片（可带一段文本）直接写剪贴板

反向共享（3.7 起，用本机的键鼠操作 Windows）:
  Ubuntu -> Windows:
    RGW 1 <y>        请求接管，y 是进入点在本机屏幕上的归一化纵坐标（0–65535）
    RGW 0            交还控制
    RK <键名> down|up  按键
    RM <dx> <dy>     指针相对位移
    RB <按钮> down|up  鼠标按钮（1 左 2 中 3 右 8 后退 9 前进）
    RW up|down|left|right  滚轮
  Windows -> Ubuntu:
    RGW ok / RGW no <原因>   接受 / 拒绝（disabled 表示设置里关了，busy 表示正忙）
    RGW 0            本机收回，让 Ubuntu 放开抓取
    RP <x> <y>       把本机指针放到绝对坐标（交还时的落点）
    SW <w> <h> <side>  Windows 的屏幕尺寸和相对方位（left/right/top/bottom）
    OWN 0|1          Windows 是否正在把键鼠送到本机（1 时禁止反向接管）

注入策略:
  ASCII            -> xdotool type（真实按键）
  非 ASCII(中文等) -> 写入剪贴板(CLIPBOARD+PRIMARY) + 按窗口类别发送粘贴键
  粘贴键按窗口类选择, 可用 ~/.config/typebridge/routes.json 覆盖:
      {"窗口类名小写": "ctrl_v" | "ctrl_shift_v" | "shift_insert" | "type"}
"""
import ctypes
import faulthandler
import json
import os
import re
import shutil
import socket
import subprocess
import sys
import threading
import time

faulthandler.enable()

# ---- 必须在任何 Gtk/Gdk 调用之前选定 DISPLAY ----
# Gtk.Clipboard.get() 在模块导入阶段就会初始化 GTK；写死 :0 会让整个 GUI 栈失效。
def _session_display_hint():
    """会话真正的 DISPLAY。

    systemd 用户服务里的 DISPLAY 常常被写死成 :0，而登录后 X server 可能是 :1/:2；
    /tmp/.X11-unix 里还可能躺着别的 X 服务器（Xvfb 之类的虚拟屏）。只按 socket
    新旧排序会连到虚拟屏上——表现就是"键鼠注进去了但屏幕没反应"。
    用户管理器的环境变量是会话真实值，优先采信。
    """
    try:
        result = subprocess.run(["systemctl", "--user", "show-environment"],
                                stdout=subprocess.PIPE, stderr=subprocess.DEVNULL,
                                timeout=2)
        for line in result.stdout.decode("utf-8", "replace").splitlines():
            if line.startswith("DISPLAY="):
                return line.split("=", 1)[1].strip()
    except Exception:
        pass
    return ""


def _early_pick_display():
    try:
        import ctypes
        lib = ctypes.CDLL("libX11.so.6")
        lib.XOpenDisplay.restype = ctypes.c_void_p
        lib.XOpenDisplay.argtypes = [ctypes.c_char_p]
        lib.XCloseDisplay.argtypes = [ctypes.c_void_p]
    except Exception:
        return False
    auths = [
        os.environ.get("XAUTHORITY", ""),
        "/run/user/%d/gdm/Xauthority" % os.getuid(),
        os.path.expanduser("~/.Xauthority"),
    ]
    displays = []
    if os.environ.get("DISPLAY"):
        displays.append(os.environ["DISPLAY"])
    hint = _session_display_hint()
    if hint and hint not in displays:
        displays.append(hint)
    try:
        socks = []
        for name in os.listdir("/tmp/.X11-unix"):
            if name.startswith("X") and name[1:].isdigit():
                path = os.path.join("/tmp/.X11-unix", name)
                socks.append((os.path.getmtime(path), ":" + name[1:]))
        for _, display in sorted(socks, reverse=True):
            if display not in displays:
                displays.append(display)
    except OSError:
        pass
    for display in displays:
        for path in auths:
            if path and os.path.exists(path):
                os.environ["XAUTHORITY"] = path
            try:
                dpy = lib.XOpenDisplay(display.encode())
            except Exception:
                dpy = None
            if dpy:
                os.environ["DISPLAY"] = display
                try:
                    lib.XCloseDisplay(dpy)
                except Exception:
                    pass
                return True
    return False


_early_pick_display()

import gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, Gdk, GLib, GdkPixbuf



CONF_DIR = os.path.expanduser("~/.config/typebridge")
CONF_FILE = os.path.join(CONF_DIR, "config.json")
ROUTES_FILE = os.path.join(CONF_DIR, "routes.json")

DEFAULT_CONF = {
    "bind": "0.0.0.0",
    "port": 4321,
    "file_port": 4322,               # 文件接收端口，0 表示关闭
    "file_dir": "",                 # 空 = 自动用系统桌面目录
    "auto_deactivate_fcitx": False,  # 本项目的拼音由 Ubuntu 输入法处理，不能自动关闭 fcitx
    "xdotool_delay_ms": 5,           # ASCII 逐字间隔
    # ---- 反向共享（用本机键鼠操作 Windows）----
    "reverse_share": True,           # 关掉后不抓全局热键、也不做边缘进入
    # 默认和 Windows 端的切换键用同一个：坐在哪台机器前都按 Ctrl+Alt+U，
    # 键鼠就交给另一台。别用 Ctrl+Alt+W —— 那是微信的全局热键，会被它先吃掉。
    "hotkey": "ctrl+alt+u",
    "edge_entry": True,              # 把指针顶到共享边并停住 0.5 秒也能切过去
}

SHIFT_INSERT_CLASSES = {"xterm", "urxvt", "st", "st-256color", "stterm", "rxvt"}
CTRL_SHIFT_V_CLASSES = ("gnome-terminal", "konsole", "kitty", "alacritty",
                        "terminator", "tilix", "wezterm", "xfce4-terminal", "guake")

KEYMAP = {
    "BackSpace": "BackSpace", "Return": "Return", "Tab": "Tab",
    "Escape": "Escape", "Delete": "Delete", "Home": "Home", "End": "End",
    "Up": "Up", "Down": "Down", "Left": "Left", "Right": "Right",
    "Prior": "Prior", "Next": "Next",
    "PageUp": "Prior", "PageDown": "Next",
    "Page_Up": "Prior", "Page_Down": "Next",
    "Space": "space", "space": "space",
    "Insert": "Insert",
    "CapsLock": "Caps_Lock", "NumLock": "Num_Lock", "ScrollLock": "Scroll_Lock",
    "PrintScreen": "Print", "Pause": "Pause", "Menu": "Menu",
    "semicolon": "semicolon", "slash": "slash", "grave": "grave",
    "bracketleft": "bracketleft", "bracketright": "bracketright",
    "minus": "minus", "equal": "equal", "comma": "comma", "period": "period",
    "backslash": "backslash", "apostrophe": "apostrophe",
    "ctrl": "Control_L", "shift": "Shift_L", "alt": "Alt_L", "super": "Super_L",
    "Win": "Super_L", "Super": "Super_L", "Meta": "Super_L",
    "KP_0": "0", "KP_1": "1", "KP_2": "2", "KP_3": "3",
    "KP_4": "4", "KP_5": "5", "KP_6": "6", "KP_7": "7",
    "KP_8": "8", "KP_9": "9",
    "KP_Multiply": "asterisk", "KP_Add": "plus",
    "KP_Separator": "comma", "KP_Subtract": "minus",
    "KP_Decimal": "period", "KP_Divide": "slash",
    "VolumeMute": "XF86AudioMute",
    "VolumeDown": "XF86AudioLowerVolume",
    "VolumeUp": "XF86AudioRaiseVolume",
    "MediaNextTrack": "XF86AudioNext",
    "MediaPrevTrack": "XF86AudioPrev",
    "MediaStop": "XF86AudioStop",
    "MediaPlayPause": "XF86AudioPlay",
}

MODIFIERS = {
    "ctrl": "ctrl", "control": "ctrl",
    "alt": "alt", "shift": "shift",
    "super": "super", "win": "super", "meta": "super",
}

for _index in range(1, 25):
    KEYMAP["F%d" % _index] = "F%d" % _index

for _letter in "abcdefghijklmnopqrstuvwxyz":
    KEYMAP[_letter] = _letter

for _digit in "0123456789":
    KEYMAP[_digit] = _digit

KEYMAP_CASEFOLD = {name.lower(): value for name, value in KEYMAP.items()}


def normalize_key(payload):
    """把 K 协议负载规范化成 xdotool key 字符串。

    支持两种形式:
      K Left
      K ctrl+alt+u / K ctrl+shift+Tab / K super+e
    只允许白名单键名与 ctrl/alt/shift/super 修饰键，避免执行任意键名。
    """
    raw = (payload or "").strip().replace(" ", "")
    if not raw:
        return None
    parts = raw.split("+")
    if not parts or any(part == "" for part in parts):
        return None

    modifiers = []
    for part in parts[:-1]:
        modifier = MODIFIERS.get(part.lower())
        if modifier is None or modifier in modifiers:
            return None
        modifiers.append(modifier)

    base = KEYMAP.get(parts[-1])
    if base is None:
        base = KEYMAP_CASEFOLD.get(parts[-1].lower())
    if base is None:
        return None

    ordered = [item for item in ("ctrl", "alt", "shift", "super") if item in modifiers]
    return "+".join(ordered + [base])


CONF = dict(DEFAULT_CONF)
ROUTES = {}
CONNS = {}   # fileno -> {"sock":, "addr":, "buf": bytes}
LAST_PEER = {"addr": None}   # 最近一个非本机客户端（Windows）


def log(msg):
    print("[typebridge %s] %s" % (time.strftime("%H:%M:%S"), msg), flush=True)


def load_json(path, default):
    try:
        with open(path, "r", encoding="utf-8") as f:
            data = json.load(f)
            if isinstance(data, dict):
                return data
    except Exception:
        pass
    return default


def xdo(*args):
    try:
        r = subprocess.run(["xdotool", *args], capture_output=True, text=True, timeout=5)
        if r.returncode != 0 and (r.stderr or "").strip():
            log("xdotool %s -> rc=%s err=%s" % (args[0], r.returncode, (r.stderr or "").strip()[:120]))
        return r
    except Exception as e:
        log("xdotool %s exception: %r" % (args[0], e))
        return None


def focused_class():
    r = xdo("getwindowfocus")
    if not (r and r.returncode == 0):
        return ""
    wid = (r.stdout or "").strip()
    if not wid:
        return ""
    try:
        r2 = subprocess.run(["xprop", "-id", wid, "WM_CLASS"],
                            capture_output=True, text=True, timeout=3)
        if r2.returncode == 0:
            m = re.findall(r'"([^"]*)"', r2.stdout or "")
            if m:
                return m[-1].lower()
    except Exception:
        pass
    return ""


def fcitx_active():
    if not CONF.get("auto_deactivate_fcitx"):
        return False
    try:
        r = subprocess.run(["fcitx-remote"], capture_output=True, text=True, timeout=2)
        return (r.stdout or "").strip() == "1"
    except Exception:
        return False


def route_for(cls):
    if cls in ROUTES:
        return ROUTES[cls]
    if cls in SHIFT_INSERT_CLASSES:
        return "shift_insert"
    if any(t in cls for t in CTRL_SHIFT_V_CLASSES):
        return "ctrl_shift_v"
    return "ctrl_v"


def paste_key(route):
    return {"shift_insert": "shift+Insert",
            "ctrl_shift_v": "ctrl+shift+v"}.get(route, "ctrl+v")


def type_ascii(s):
    if fcitx_active():
        subprocess.run(["fcitx-remote", "-c"], capture_output=True, timeout=2)
        time.sleep(0.05)
    xdo("type", "--clearmodifiers", "--delay", str(CONF.get("xdotool_delay_ms", 5)), s)


def pump_events(ms):
    """泵 GTK 主循环, 及时应答目标程序发来的剪贴板选择权请求"""
    end = time.time() + ms / 1000.0
    while time.time() < end:
        while Gtk.events_pending():
            Gtk.main_iteration_do(False)
        time.sleep(0.005)


def paste_text(s, cls):
    """写剪贴板后发粘贴键。必须在主线程运行, 并及时泵事件循环。"""
    try:
        for sel in (Gdk.SELECTION_CLIPBOARD, Gdk.SELECTION_PRIMARY):
            cb = Gtk.Clipboard.get(sel)
            cb.set_text(s, -1)
            cb.store()
    except Exception as e:
        log("clipboard error: %r" % (e,))
    pump_events(30)
    xdo("key", "--clearmodifiers", paste_key(route_for(cls)))
    pump_events(60)


def inject_text(text):
    cls = focused_class()
    log("inject %d chars, target class=%r" % (len(text), cls))
    runs = []   # ('a', ASCII串) / ('c', 中文串) / ('r', 回车)
    for ch in text:
        if ch == "\n":
            runs.append(("r", ""))
        elif ord(ch) < 128:
            if runs and runs[-1][0] == "a":
                runs[-1] = ("a", runs[-1][1] + ch)
            else:
                runs.append(("a", ch))
        else:
            if runs and runs[-1][0] == "c":
                runs[-1] = ("c", runs[-1][1] + ch)
            else:
                runs.append(("c", ch))
    for kind, s in runs:
        if kind == "a":
            type_ascii(s)
        elif kind == "r":
            xdo("key", "--clearmodifiers", "Return")
        else:
            paste_text(s, cls)


def inject_key(key):
    """注入按键。锁定键不能清修饰状态，否则会改变键盘锁定状态。"""
    base = key.split("+")[-1]
    if base in {"Caps_Lock", "Num_Lock", "Scroll_Lock"}:
        xdo("key", key)
    else:
        xdo("key", "--clearmodifiers", key)


MOUSE_BUTTONS = {1, 2, 3, 8, 9}
WHEEL_BUTTONS = {"up": 4, "down": 5, "left": 6, "right": 7}
MOUSE_MAX_STEP = 2000
PRESSED_BUTTONS = set()

# 指针注入优先走 XTEST 直连：一次调用约 1 微秒。
# xdotool 每条命令都要 fork 一个进程（实测约 2.2ms），250Hz 以上根本跟不上，
# 表现就是指针一顿一顿。XTEST 不可用时才回退到 xdotool。
try:
    import ctypes

    _x11 = ctypes.CDLL("libX11.so.6")
    _xtst = ctypes.CDLL("libXtst.so.6")
    _x11.XOpenDisplay.restype = ctypes.c_void_p
    _x11.XOpenDisplay.argtypes = [ctypes.c_char_p]
    _x11.XFlush.argtypes = [ctypes.c_void_p]
    _x11.XDefaultRootWindow.restype = ctypes.c_ulong
    _x11.XDefaultRootWindow.argtypes = [ctypes.c_void_p]
    _DPY = _x11.XOpenDisplay(None)
    if _DPY:
        _xtst.XTestFakeRelativeMotionEvent.argtypes = [
            ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
        _xtst.XTestFakeMotionEvent.argtypes = [
            ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
        _xtst.XTestFakeButtonEvent.argtypes = [
            ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]
    else:
        _xtst = None
except Exception as _xtest_error:
    _DPY = None
    _xtst = None
    log("XTEST unavailable (%r)，回退到 xdotool" % (_xtest_error,))

# ---- 显示连接自愈 ----
# 停在 GDM 登录界面时 jx 没有图形会话，XOpenDisplay 会失败；登录后 cookie 重新生成。
# 这里做懒重连，登录后自动恢复，不必手动重启服务。
_X_AUTH_CANDIDATES = [
    os.environ.get("XAUTHORITY", ""),
    "/run/user/%d/gdm/Xauthority" % os.getuid(),
    os.path.expanduser("~/.Xauthority"),
]
_last_attach_try = 0.0
_attach_failures = 0


def _set_argtypes():
    _xtst.XTestFakeRelativeMotionEvent.argtypes = [
        ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
    _xtst.XTestFakeMotionEvent.argtypes = [
        ctypes.c_void_p, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
    _xtst.XTestFakeButtonEvent.argtypes = [
        ctypes.c_void_p, ctypes.c_uint, ctypes.c_int, ctypes.c_ulong]


def pick_display():
    """在 GTK 初始化之前选定真正可用的 DISPLAY。

    gdm 的登录界面在 :0，用户登录后会另起一个 X server（常见 :2）。
    写死 :0 会导致 GTK/XTEST 全部连不上，连屏幕尺寸都发不出去。
    """
    try:
        lib = _x11
    except NameError:
        return False
    for display in _display_candidates():
        for path in _X_AUTH_CANDIDATES:
            if path and os.path.exists(path):
                os.environ["XAUTHORITY"] = path
            try:
                dpy = lib.XOpenDisplay(display.encode())
            except Exception:
                dpy = None
            if dpy:
                os.environ["DISPLAY"] = display
                log("picked DISPLAY=%s XAUTHORITY=%s" % (display, os.environ.get("XAUTHORITY") or "-"))
                return True
    return False


def _display_candidates():
    """列出可能的显示：环境变量 -> 会话真实值 -> /tmp/.X11-unix 里按时间新的优先。

    只按 socket 新旧排会踩到虚拟屏（Xvfb 往往比真实 Xorg 新），先问会话要真实值。
    """
    found = []
    env = os.environ.get("DISPLAY", "")
    if env:
        found.append(env)
    hint = _session_display_hint()
    if hint and hint not in found:
        found.append(hint)
    try:
        socks = []
        for name in os.listdir("/tmp/.X11-unix"):
            if name.startswith("X") and name[1:].isdigit():
                path = os.path.join("/tmp/.X11-unix", name)
                socks.append((os.path.getmtime(path), ":" + name[1:]))
        for _, display in sorted(socks, reverse=True):
            if display not in found:
                found.append(display)
    except OSError:
        pass
    return found


def ensure_display(force=False):
    """没有显示连接时尝试重连（限速 2 秒一次），连上返回 True。"""
    global _DPY, _last_attach_try, _attach_failures
    if _DPY:
        return True
    if _xtst is None:
        return False
    now = time.time()
    if not force and now - _last_attach_try < 2.0:
        return False
    _last_attach_try = now
    try:
        for display in _display_candidates():
            for path in _X_AUTH_CANDIDATES:
                if path and os.path.exists(path):
                    os.environ["XAUTHORITY"] = path
                dpy = _x11.XOpenDisplay(display.encode())
                if dpy:
                    _DPY = dpy
                    os.environ["DISPLAY"] = display
                    _set_argtypes()
                    _attach_failures = 0
                    log("X display attached DISPLAY=%s XAUTHORITY=%s"
                        % (display, os.environ.get("XAUTHORITY") or "-"))
                    return True
    except Exception as e:
        log("X reconnect error: %r" % (e,))
    _attach_failures += 1
    if _attach_failures == 1 or _attach_failures % 60 == 0:
        log("X display unavailable x%d（登录桌面后会自动重连）" % _attach_failures)
    return False


_pending_dx = 0
_pending_dy = 0


def pointer_move(dx, dy):
    if ensure_display():
        _xtst.XTestFakeRelativeMotionEvent(_DPY, dx, dy, 0)
        _x11.XFlush(_DPY)
    else:
        xdo("mousemove_relative", "--", str(dx), str(dy))


def pointer_button(button, down):
    if ensure_display():
        _xtst.XTestFakeButtonEvent(_DPY, button, 1 if down else 0, 0)
        _x11.XFlush(_DPY)
    else:
        xdo("mousedown" if down else "mouseup", str(button))


def pointer_wheel(button):
    if ensure_display():
        _xtst.XTestFakeButtonEvent(_DPY, button, 1, 0)
        _xtst.XTestFakeButtonEvent(_DPY, button, 0, 0)
        _x11.XFlush(_DPY)
    else:
        xdo("click", str(button))


def flush_move():
    """把积攒的位移一次性注入。一批里连着多条 M 时合并成一次 X 调用。"""
    global _pending_dx, _pending_dy
    dx, dy = _pending_dx, _pending_dy
    _pending_dx = 0
    _pending_dy = 0
    if dx or dy:
        pointer_move(dx, dy)


def release_mouse_buttons(reason):
    """断开连接时松开仍按下的按钮，否则会卡在拖拽状态。"""
    if not PRESSED_BUTTONS:
        return
    flush_move()
    for button in sorted(PRESSED_BUTTONS):
        try:
            pointer_button(button, False)
        except Exception as e:
            log("mouse release failed: %r" % (e,))
    log("released mouse buttons %s (%s)" % (sorted(PRESSED_BUTTONS), reason))
    PRESSED_BUTTONS.clear()


# ---- 指针可见性 ----
# 鼠标切回 Windows 后，Ubuntu 这边会留一个 "僵在半路" 的指针。用 XFixes
# 把它藏起来；一旦键盘还留在 Ubuntu（只用键盘的场景），指针要照常显示。
_xfixes = None
_pointer_hidden = {"value": False, "timer": 0}
# 兜底：万一之后有人直接在本机用 Ubuntu（不经 Windows 桥），不能留一个永远看不见的指针
POINTER_HIDE_TIMEOUT = 600


def _pointer_timeout():
    _pointer_hidden["timer"] = 0
    if _pointer_hidden["value"]:
        log("cursor: 隐藏超时，自动恢复显示")
        set_pointer_visible(True)
    return False


def _load_xfixes():
    global _xfixes
    if _xfixes is not None:
        return _xfixes
    try:
        lib = ctypes.CDLL("libXfixes.so.3")
        lib.XFixesHideCursor.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        lib.XFixesHideCursor.restype = None
        lib.XFixesShowCursor.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        lib.XFixesShowCursor.restype = None
        _xfixes = lib
    except Exception as e:
        log("xfixes unavailable (%r)，指针隐藏不可用" % (e,))
        _xfixes = False
    return _xfixes


def _apply_show(lib, force=False):
    root = _x11.XDefaultRootWindow(_DPY)
    if _pointer_hidden["value"] or force:
        lib.XFixesShowCursor(_DPY, root)
        _pointer_hidden["value"] = False
        log("cursor: 显示")


def _apply_hide(lib):
    _pointer_hidden["pending"] = 0
    root = _x11.XDefaultRootWindow(_DPY)
    if not _pointer_hidden["value"]:
        lib.XFixesHideCursor(_DPY, root)
        _pointer_hidden["value"] = True
        log("cursor: 隐藏（反向接管中）")
        _pointer_hidden["timer"] = GLib.timeout_add_seconds(
            POINTER_HIDE_TIMEOUT, _pointer_timeout)
    return False


def _cancel_pending_hide():
    pending = _pointer_hidden.get("pending") or 0
    if pending:
        try:
            GLib.source_remove(pending)
        except Exception:
            pass
        _pointer_hidden["pending"] = 0


def set_pointer_visible(visible, force=False):
    global _DPY
    if not _DPY:
        return
    lib = _load_xfixes()
    if not lib:
        return
    _cancel_pending_hide()
    try:
        if visible:
            # force：新进程不知道上一个进程有没有把指针藏起来，启动时无条件还原一次
            _apply_show(lib, force)
            if _pointer_hidden["timer"]:
                try:
                    GLib.source_remove(_pointer_hidden["timer"])
                except Exception:
                    pass
                _pointer_hidden["timer"] = 0
        else:
            # 来回切换很快时不立刻隐藏，等 250ms 定了再藏，避免指针闪一下
            _pointer_hidden["pending"] = GLib.timeout_add(250, lambda: _apply_hide(lib))
        _x11.XFlush(_DPY)
    except Exception as e:
        log("cursor toggle failed: %r" % (e,))


def ensure_pointer_visible():
    """Windows 真的在驱动这台机器时，指针必须是可见的（防止状态不同步时"什么都看不到"）。"""
    if _pointer_hidden["value"] or _pointer_hidden.get("pending"):
        set_pointer_visible(True)


def mouse_move(payload):
    """M <dx> <dy>：相对移动指针。先累加，等这批 M 行处理完由 flush_move 一次注入。"""
    global _pending_dx, _pending_dy
    ensure_pointer_visible()
    parts = payload.split()
    if len(parts) != 2:
        log("bad M payload: %r" % (payload,))
        return
    try:
        dx = int(parts[0])
        dy = int(parts[1])
    except ValueError:
        log("bad M payload: %r" % (payload,))
        return
    dx = max(-MOUSE_MAX_STEP, min(MOUSE_MAX_STEP, dx))
    dy = max(-MOUSE_MAX_STEP, min(MOUSE_MAX_STEP, dy))
    _pending_dx = max(-MOUSE_MAX_STEP, min(MOUSE_MAX_STEP, _pending_dx + dx))
    _pending_dy = max(-MOUSE_MAX_STEP, min(MOUSE_MAX_STEP, _pending_dy + dy))


def mouse_button(payload):
    """B <按钮> down|up：按下/松开按钮，拖拽靠 down 与 up 的配对实现。"""
    parts = payload.split()
    if len(parts) != 2 or parts[1] not in ("down", "up"):
        log("bad B payload: %r" % (payload,))
        return
    try:
        button = int(parts[0])
    except ValueError:
        log("bad B payload: %r" % (payload,))
        return
    if button not in MOUSE_BUTTONS:
        log("unsupported mouse button: %r" % (payload,))
        return
    flush_move()   # 点击必须落在最新位置
    if parts[1] == "down":
        PRESSED_BUTTONS.add(button)
    else:
        PRESSED_BUTTONS.discard(button)
    pointer_button(button, parts[1] == "down")


def mouse_wheel(payload):
    """W up|down|left|right：滚轮。"""
    button = WHEEL_BUTTONS.get(payload.strip())
    if button is None:
        log("bad W payload: %r" % (payload,))
        return
    flush_move()
    pointer_wheel(button)


def pointer_move_abs(payload):
    """P <x> <y>：把指针移到远端屏幕的绝对位置（切屏进入时用）。"""
    ensure_pointer_visible()
    parts = payload.split()
    if len(parts) != 2:
        log("bad P payload: %r" % (payload,))
        return
    try:
        x = int(parts[0])
        y = int(parts[1])
    except ValueError:
        log("bad P payload: %r" % (payload,))
        return
    x = max(0, min(65535, x))
    y = max(0, min(65535, y))
    if _DPY:
        _xtst.XTestFakeMotionEvent(_DPY, -1, x, y, 0)
        _x11.XFlush(_DPY)
    else:
        xdo("mousemove", str(x), str(y))


# ---------- 反向共享：用本机的键鼠去操作 Windows ----------
# 抓取走 X11 主动抓取（XGrabKeyboard / XGrabPointer）：抓取期间按键、点击、滚轮
# 全部只送到本进程，本机窗口收不到 —— 等于把整套键鼠借给了 Windows。
# 反方向（Windows -> Ubuntu）走 XTEST 注入，两条路互不干扰。
#
# 退出路径必须彻底：热键、Windows 收回、连接断开、心跳静默都会放开抓取。
# X 只在本进程退出时才自动释放抓取，所以任何一条退出路径漏了，用户就被锁在自己机器外面。

_RWIN = {
    "active": False,       # 正在抓取本机输入
    "confirmed": False,    # Windows 回了 ok
    "sock": None,          # 事件发往哪条连接
    "deadline": 0.0,       # 等 ok 的截止时间
    "side": "",            # Windows 在本机的哪一侧
    "win_w": 0,
    "win_h": 0,
    "sw": 0,               # 本机屏幕
    "sh": 0,
    "down": set(),         # 已通知 Windows 按下的键名
    "buttons": set(),      # 已按下的鼠标键
    "dx": 0,
    "dy": 0,
    "last_x": 0,
    "last_y": 0,
    "ignore_motion": 0,    # 刚 warp 过，下一个 motion 只用来对齐坐标
    "entry": (0, 0),
    "win_owns": False,     # Windows 正把键鼠送到本机（OWN 1），此时不许反向接管
}
_HOT = {"code": 0, "mask": 0, "tried": 0.0, "cooldown": 0.0}
_EDGE = {"since": 0.0, "armed": True, "px": -1, "py": -1, "cooldown": 0.0}
_LOCAL_MOVE = {"t": 0.0}
# 最近一次收到对端注入（K/M/B/W）的时间：用来分辨「本机真的有人在推」
# 和「对端正在驱动这台机器」——后者指针也会动到边上，不能当成切换意图。
LAST_INPUT_IN = {"t": 0.0}
LAST_RX = {"t": time.time()}
_KEYCODE_NAME = {}
_XCAP_READY = {"done": False}


class _XEvent(ctypes.Structure):
    """XEvent 的前半段：KeyPress / ButtonPress / MotionNotify 的公共字段到 state 都一样，
    第四个差别字段（keycode / button / is_hint）落在同一个偏移上，共用一个结构足够。"""
    _fields_ = [
        ("type", ctypes.c_int),
        ("serial", ctypes.c_ulong),
        ("send_event", ctypes.c_int),
        ("display", ctypes.c_void_p),
        ("window", ctypes.c_ulong),
        ("root", ctypes.c_ulong),
        ("subwindow", ctypes.c_ulong),
        ("time", ctypes.c_ulong),
        ("x", ctypes.c_int),
        ("y", ctypes.c_int),
        ("x_root", ctypes.c_int),
        ("y_root", ctypes.c_int),
        ("state", ctypes.c_uint),
        ("code", ctypes.c_uint),
        ("same_screen", ctypes.c_int),
    ]


_EV_BUF = ctypes.create_string_buffer(256)

_KEY_EVENT, _BUTTON_EVENT, _MOTION_EVENT = 2, 4, 6
_KEY_RELEASE, _BUTTON_RELEASE = 3, 5

# X11 键名 -> 协议键名。只放 Windows 端认识的那些，其余一律丢弃。
_KEYSYM_NAMES = {
    "Control_L": "ctrl", "Control_R": "ctrl",
    "Shift_L": "shift", "Shift_R": "shift",
    "Alt_L": "alt", "Alt_R": "alt",
    "Meta_L": "super", "Meta_R": "super",
    "Super_L": "super", "Super_R": "super",
    "Caps_Lock": "CapsLock", "Num_Lock": "NumLock", "Scroll_Lock": "ScrollLock",
    "Print": "PrintScreen", "Sys_Req": "PrintScreen",
    "KP_Enter": "KP_Enter", "Menu": "Menu",
    "XF86AudioMute": "VolumeMute", "XF86AudioLowerVolume": "VolumeDown",
    "XF86AudioRaiseVolume": "VolumeUp", "XF86AudioNext": "MediaNextTrack",
    "XF86AudioPrev": "MediaPrevTrack", "XF86AudioStop": "MediaStop",
    "XF86AudioPlay": "MediaPlayPause",
}

_KEYSYM_PLAIN = {
    "Return", "BackSpace", "Tab", "Escape", "Delete", "Insert",
    "Home", "End", "Prior", "Next", "Up", "Down", "Left", "Right", "space",
    "Pause", "semicolon", "slash", "grave", "bracketleft", "bracketright",
    "minus", "equal", "comma", "period", "backslash", "apostrophe",
}

_KP_NAMES = {"KP_0", "KP_1", "KP_2", "KP_3", "KP_4", "KP_5", "KP_6", "KP_7",
             "KP_8", "KP_9", "KP_Multiply", "KP_Add", "KP_Separator",
             "KP_Subtract", "KP_Decimal", "KP_Divide"}


def _normalize_keysym(name):
    """X11 键名归一成协议词表；不认识的返回 None。"""
    if not name:
        return None
    if name in _KEYSYM_NAMES:
        return _KEYSYM_NAMES[name]
    if name in _KEYSYM_PLAIN or name in _KP_NAMES:
        return name
    if len(name) == 1 and name.isprintable():
        return name
    if len(name) >= 2 and name[0] == "F" and name[1:].isdigit() and 1 <= int(name[1:]) <= 24:
        return name
    return None


def _xcap_ready():
    """按需装配抓取要用的 Xlib 原型。"""
    if _XCAP_READY["done"]:
        return True
    if _x11 is None:
        return False
    try:
        _x11.XConnectionNumber.restype = ctypes.c_int
        _x11.XConnectionNumber.argtypes = [ctypes.c_void_p]
        _x11.XPending.restype = ctypes.c_int
        _x11.XPending.argtypes = [ctypes.c_void_p]
        _x11.XNextEvent.argtypes = [ctypes.c_void_p, ctypes.c_void_p]
        _x11.XSync.argtypes = [ctypes.c_void_p, ctypes.c_int]
        _x11.XGrabKeyboard.restype = ctypes.c_int
        _x11.XGrabKeyboard.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int,
                                       ctypes.c_int, ctypes.c_int, ctypes.c_ulong]
        _x11.XUngrabKeyboard.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        _x11.XGrabPointer.restype = ctypes.c_int
        _x11.XGrabPointer.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_int,
                                      ctypes.c_uint, ctypes.c_int, ctypes.c_int,
                                      ctypes.c_ulong, ctypes.c_ulong, ctypes.c_ulong]
        _x11.XUngrabPointer.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        _x11.XWarpPointer.argtypes = [ctypes.c_void_p, ctypes.c_ulong, ctypes.c_ulong,
                                      ctypes.c_int, ctypes.c_int, ctypes.c_uint,
                                      ctypes.c_uint, ctypes.c_int, ctypes.c_int]
        _x11.XQueryPointer.restype = ctypes.c_int
        _x11.XQueryPointer.argtypes = [
            ctypes.c_void_p, ctypes.c_ulong,
            ctypes.POINTER(ctypes.c_ulong), ctypes.POINTER(ctypes.c_ulong),
            ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_int), ctypes.POINTER(ctypes.c_int),
            ctypes.POINTER(ctypes.c_uint)]
        _x11.XStringToKeysym.restype = ctypes.c_ulong
        _x11.XStringToKeysym.argtypes = [ctypes.c_char_p]
        _x11.XKeysymToKeycode.restype = ctypes.c_ubyte
        _x11.XKeysymToKeycode.argtypes = [ctypes.c_void_p, ctypes.c_ulong]
        _x11.XKeysymToString.restype = ctypes.c_char_p
        _x11.XKeysymToString.argtypes = [ctypes.c_ulong]
        _x11.XDisplayKeycodes.argtypes = [ctypes.c_void_p, ctypes.POINTER(ctypes.c_int),
                                          ctypes.POINTER(ctypes.c_int)]
        _x11.XGetKeyboardMapping.restype = ctypes.POINTER(ctypes.c_ulong)
        _x11.XGetKeyboardMapping.argtypes = [ctypes.c_void_p, ctypes.c_ubyte,
                                             ctypes.c_int, ctypes.POINTER(ctypes.c_int)]
        _x11.XGrabKey.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_uint,
                                  ctypes.c_ulong, ctypes.c_int, ctypes.c_int, ctypes.c_int]
        _x11.XUngrabKey.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_uint, ctypes.c_ulong]
        _x11.XkbSetDetectableAutoRepeat.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
        # 回调必须按 CFUNCTYPE 声明实参：转成 c_void_p 再传会被 ctypes 拒绝
        _x11.XSetErrorHandler.restype = ctypes.c_void_p
        _x11.XSetErrorHandler.argtypes = [_XERR_CB]
        _XCAP_READY["done"] = True
        return True
    except Exception as e:
        log("reverse share: X11 抓取接口不可用 %r" % (e,))
        return False


# Xlib 默认的错误处理会 exit(1)：热键被别的程序占用时一次 BadAccess 就能
# 直接把守护进程带走。接管错误处理后只记账、不退出。
_XERR = {"codes": [], "armed": False}
_XERR_CB = ctypes.CFUNCTYPE(ctypes.c_int, ctypes.c_void_p, ctypes.c_void_p)


@_XERR_CB
def _on_x_error(dpy, event):
    try:
        if _XERR["armed"] and event:
            base = 32 if ctypes.sizeof(ctypes.c_void_p) == 8 else 16
            raw = ctypes.cast(event, ctypes.POINTER(ctypes.c_ubyte * 64)).contents
            _XERR["codes"].append((raw[base], raw[base + 1]))
    except Exception:
        pass
    return 0


def _install_error_handler():
    try:
        _x11.XSetErrorHandler(_on_x_error)
    except Exception as e:
        log("reverse share: 安装 X 错误处理器失败 %r" % (e,))


def _screen_size():
    try:
        screen = Gdk.Screen.get_default()
        return screen.get_width(), screen.get_height()
    except Exception:
        return 1920, 1080


def _root_window():
    return _x11.XDefaultRootWindow(_DPY)


def _query_pointer():
    """返回 (x, y, modifier_mask)，拿不到返回 None。"""
    if not _DPY:
        return None
    try:
        root = _root_window()
        rr, ch = ctypes.c_ulong(), ctypes.c_ulong()
        rx, ry, wx, wy = ctypes.c_int(), ctypes.c_int(), ctypes.c_int(), ctypes.c_int()
        mask = ctypes.c_uint()
        ok = _x11.XQueryPointer(_DPY, root, ctypes.byref(rr), ctypes.byref(ch),
                                ctypes.byref(rx), ctypes.byref(ry),
                                ctypes.byref(wx), ctypes.byref(wy), ctypes.byref(mask))
        if not ok:
            return None
        return rx.value, ry.value, mask.value
    except Exception:
        return None


def _warp(x, y):
    if not _DPY or not _xcap_ready():
        # 原型没装配就调 XWarpPointer，_DPY 会被当成 32 位 int 传进去、直接段错误
        return
    try:
        _x11.XWarpPointer(_DPY, 0, _root_window(), 0, 0, 0, 0, int(x), int(y))
        _x11.XFlush(_DPY)
    except Exception as e:
        log("warp pointer failed: %r" % (e,))


def _build_keymap():
    """keycode -> 协议键名。取每个 keycode 的第一个 keysym（未加修饰的那个），
    这样 Shift 交给对端自己算，跨键盘布局的字母数字才不会错位。"""
    if not _DPY or not _xcap_ready():
        return
    try:
        mn, mx = ctypes.c_int(), ctypes.c_int()
        _x11.XDisplayKeycodes(_DPY, ctypes.byref(mn), ctypes.byref(mx))
        count = mx.value - mn.value + 1
        if count <= 0:
            return
        per = ctypes.c_int()
        arr = _x11.XGetKeyboardMapping(_DPY, mn.value, count, ctypes.byref(per))
        if not arr or per.value <= 0:
            return
        _KEYCODE_NAME.clear()
        for index in range(count):
            keysym = arr[index * per.value]
            if not keysym:
                continue
            raw = _x11.XKeysymToString(keysym)
            if not raw:
                continue
            name = _normalize_keysym(raw.decode("utf-8", "replace"))
            if name:
                _KEYCODE_NAME[mn.value + index] = name
        log("reverse share: 键位表 %d 个键" % len(_KEYCODE_NAME))
    except Exception as e:
        log("reverse share: 读键盘映射失败 %r" % (e,))


# ---- 发给 Windows 的事件（带输出缓冲，非阻塞 socket 上不会写坏半行） ----

def _peer_sock():
    if not CONNS:
        return None
    try:
        return list(CONNS.values())[-1]["sock"]
    except Exception:
        return None


def _conn_send(sock, data):
    if sock is None:
        return False
    st = CONNS.get(sock.fileno())
    if st is None:
        try:
            sock.sendall(data)
            return True
        except OSError:
            return False
    st["out"] = (st.get("out") or b"") + data
    if len(st["out"]) > (4 << 20):
        drop(sock.fileno(), "reverse output overflow")
        return False
    return _conn_flush(sock.fileno())


def _conn_flush(fd):
    st = CONNS.get(fd)
    if st is None:
        return False
    buf = st.get("out") or b""
    while buf:
        try:
            sent = st["sock"].send(buf)
        except (BlockingIOError, InterruptedError):
            break
        except OSError:
            drop(fd, "reverse send failed")
            return False
        if sent <= 0:
            break
        buf = buf[sent:]
    st["out"] = buf
    if buf:
        if not st.get("out_watch"):
            st["out_watch"] = GLib.io_add_watch(
                fd, GLib.IO_OUT | GLib.IO_HUP | GLib.IO_ERR, _on_writable)
    elif st.get("out_watch"):
        try:
            GLib.source_remove(st["out_watch"])
        except Exception:
            pass
        st["out_watch"] = 0
    return True


def _on_writable(fd, cond):
    if CONNS.get(fd) is None:
        return False
    if cond & (GLib.IO_HUP | GLib.IO_ERR):
        return drop(fd, "closed while writing")
    return _conn_flush(fd)


def _send_event(line):
    if CONF.get("debug_input"):
        log("reverse -> %s" % line)
    return _conn_send(_RWIN.get("sock"), (line + "\n").encode("utf-8"))


def _flush_remote_move():
    dx, dy = _RWIN["dx"], _RWIN["dy"]
    if not dx and not dy:
        return
    _RWIN["dx"] = 0
    _RWIN["dy"] = 0
    dx = max(-MOUSE_MAX_STEP, min(MOUSE_MAX_STEP, dx))
    dy = max(-MOUSE_MAX_STEP, min(MOUSE_MAX_STEP, dy))
    _send_event("RM %d %d" % (dx, dy))


# ---- 抓取 / 释放 ----

def _ungrab():
    if not _DPY:
        return
    try:
        _x11.XUngrabPointer(_DPY, 0)
        _x11.XUngrabKeyboard(_DPY, 0)
        _x11.XSync(_DPY, 0)
    except Exception as e:
        log("reverse share: ungrab failed %r" % (e,))


def _warp_inside():
    w, h = _RWIN["sw"], _RWIN["sh"]
    if not w or not h:
        return
    gap = 40
    x, y = _RWIN["last_x"], _RWIN["last_y"]
    side = _RWIN["side"]
    if side == "left":
        x = gap
    elif side == "right":
        x = w - gap
    elif side == "top":
        y = gap
    elif side == "bottom":
        y = h - gap
    _warp(max(0, min(w - 1, x)), max(0, min(h - 1, y)))


def _enter(reason, y_norm=None):
    if _RWIN["active"]:
        return
    # 对端正驱动这台机器时，它的注入也会产生按键/位移。只有注入停下来一小会儿，
    # 才认为这次是本地有人真的在操作——否则就会两边来回抢（注入的热键触发接管）。
    if _RWIN["win_owns"] and time.time() - LAST_INPUT_IN["t"] < 0.25:
        log("reverse share: 忽略进入（对端正驱动本机且刚有注入）")
        return
    sock = _peer_sock()
    if sock is None:
        _tray_notify("还没连上 Windows", "先在 Windows 端启动 TypeBridge 并连上")
        return
    if not ensure_display() or not _xcap_ready():
        return
    if not _KEYCODE_NAME:
        _build_keymap()
    _install_error_handler()
    _attach_x_watch()

    root = _root_window()
    rc = _x11.XGrabKeyboard(_DPY, root, 0, 1, 1, 0)   # GrabModeAsync = 1
    if rc != 0:
        log("reverse share: XGrabKeyboard rc=%d" % rc)
        _tray_notify("无法接管本机键盘", "可能被别的程序占用（游戏 / 录屏），请稍后再试")
        return
    prc = _x11.XGrabPointer(_DPY, root, 0, 4 | 8 | 64, 1, 1, 0, 0, 0)
    if prc != 0:
        log("reverse share: XGrabPointer rc=%d" % prc)
        _ungrab()
        _tray_notify("无法接管本机鼠标", "可能被别的程序占用，请稍后再试")
        return
    _x11.XSync(_DPY, 0)
    try:
        _x11.XkbSetDetectableAutoRepeat(_DPY, 1, None)
    except Exception:
        pass

    pos = _query_pointer()
    if pos is None:
        _ungrab()
        return
    x, y, mods = pos
    w, h = _screen_size()

    _RWIN.update(active=True, confirmed=False, deadline=time.time() + 1.5,
                 sock=sock, sw=w, sh=h, down=set(), buttons=set(),
                 dx=0, dy=0, last_x=x, last_y=y, ignore_motion=0, entry=(x, y))
    _EDGE["since"] = 0.0
    if y_norm is None:
        y_norm = int(round(65535.0 * y / max(1, h - 1)))
    # 进入时无条件把指针挪回屏幕中间：
    # 从边缘进入时指针贴在 x=0 上，再往那边推会被屏幕边界夹住、位移全丢，
    # 表现就是「切过去了但光标不动」，然后人手往回一拨又触发了边缘切回。
    # 抓取期间指针是隐藏的，挪到中间没人看得出来，却换来了整块屏幕的移动余量。
    _warp(w // 2, h // 2)
    _RWIN["last_x"], _RWIN["last_y"] = w // 2, h // 2
    _RWIN["ignore_motion"] = 1

    set_pointer_visible(False)

    # 进入瞬间按着的修饰键要先报给对端，否则随后的组合键会缺 Ctrl
    for bit, name in ((4, "ctrl"), (8, "alt"), (1, "shift"), (64, "super")):
        if mods & bit:
            _RWIN["down"].add(name)
            _send_event("RK " + name + " down")

    _send_event("RGW 1 %d" % y_norm)
    log("reverse share: 接管本机键鼠（%s）" % reason)
    refresh_tray()


def _leave(reason, tell_windows=True, warp=True):
    if not _RWIN["active"]:
        return
    for name in sorted(_RWIN["down"]):
        _send_event("RK " + name + " up")
    for button in sorted(_RWIN["buttons"]):
        _send_event("RB %d up" % button)
    _RWIN["down"].clear()
    _RWIN["buttons"].clear()
    _flush_remote_move()
    if tell_windows:
        _send_event("RGW 0")
    _ungrab()
    set_pointer_visible(True)
    # 交还后的 0.5 秒内忽略热键：X 的自动重复/按键释放顺序会让同一次按压
    # 再触发一次 _enter，表现就是「刚按完又自己切回去」。
    _HOT["cooldown"] = time.time() + 0.5
    _RWIN.update(active=False, confirmed=False, sock=None, deadline=0.0)
    # 交还后先别急着允许边缘再进：指针还压在边上，否则会立刻又切走
    _EDGE["since"] = 0.0
    _EDGE["armed"] = False
    # 交还后 0.35 秒内不再判定进入：指针刚被摆回边界内侧（40px），
    # 再短会误判，再长就是「想马上切回去却要干等」。
    _EDGE["cooldown"] = time.time() + 0.35
    _LOCAL_MOVE["t"] = 0.0
    if warp:
        _warp_inside()
    log("reverse share: 交还本机（%s）" % reason)
    refresh_tray()


# ---- 抓取期间的 X 事件 ----

def _is_hotkey(keycode, state):
    code = _HOT["code"]
    if not code or keycode != code:
        return False
    mask = _HOT["mask"]
    if mask == 0:
        return True
    return (state & mask) == mask


def _on_x_key(ev, down):
    if not _RWIN["active"]:
        # 刚用热键交还时不要立刻又被同一次按键的重复事件接管回去
        if down and _is_hotkey(ev.code, ev.state) and time.time() > _HOT["cooldown"]:
            _enter("热键")
        return
    name = _KEYCODE_NAME.get(ev.code)
    if name is None:
        return
    if _is_hotkey(ev.code, ev.state):
        if down:
            _leave("热键交还", True)
        return
    if down:
        if name in _RWIN["down"]:
            return
        _RWIN["down"].add(name)
        if CONF.get("debug_input"):
            log("keydown %s code=%d state=%d t=%.3f" % (name, ev.code, ev.state, time.time()))
        _send_event("RK " + name + " down")
    else:
        # 进入之前就按着的键没发过 down，这里也不补 up，免得对端状态错乱
        if name not in _RWIN["down"]:
            return
        _RWIN["down"].discard(name)
        _send_event("RK " + name + " up")


def _on_x_button(ev, down):
    if not _RWIN["active"]:
        return
    button = ev.code
    if button in WHEEL_BUTTONS.values():
        if down:
            for direction, number in WHEEL_BUTTONS.items():
                if number == button:
                    _send_event("RW " + direction)
                    break
        return
    if button not in MOUSE_BUTTONS:
        return
    if down:
        if button in _RWIN["buttons"]:
            return
        _RWIN["buttons"].add(button)
        _send_event("RB %d down" % button)
    else:
        if button not in _RWIN["buttons"]:
            return
        _RWIN["buttons"].discard(button)
        _send_event("RB %d up" % button)


def _on_x_motion(ev):
    if not _RWIN["active"]:
        return
    x, y = ev.x_root, ev.y_root
    if _RWIN["ignore_motion"]:
        _RWIN["ignore_motion"] = 0
        _RWIN["last_x"], _RWIN["last_y"] = x, y
        return
    dx = x - _RWIN["last_x"]
    dy = y - _RWIN["last_y"]
    _RWIN["last_x"], _RWIN["last_y"] = x, y
    # 单帧跳几百像素必然是 warp（进入时居中 / 贴边回中）留下的位移，不是人手动作。
    # 这种假位移会瞬间把对端光标甩到屏幕边缘并触发"边缘切回"——只对齐坐标，不转发。
    if abs(dx) > 80 or abs(dy) > 80:
        return
    if dx or dy:
        _RWIN["dx"] += dx
        _RWIN["dy"] += dy
    w, h = _RWIN["sw"], _RWIN["sh"]
    if w and h and (x <= 3 or y <= 3 or x >= w - 4 or y >= h - 4):
        # 贴着屏幕边继续推，位移会被夹在边界上丢掉；挪回中间接着数
        cx, cy = w // 2, h // 2
        _warp(cx, cy)
        _RWIN["last_x"], _RWIN["last_y"] = cx, cy
        _RWIN["ignore_motion"] = 1


def _on_x_events(fd, cond):
    if not _DPY:
        return False
    drained = 0
    while _x11.XPending(_DPY):
        _x11.XNextEvent(_DPY, _EV_BUF)
        ev = ctypes.cast(_EV_BUF, ctypes.POINTER(_XEvent)).contents
        kind = ev.type
        if kind == _KEY_EVENT or kind == _KEY_RELEASE:
            _on_x_key(ev, kind == _KEY_EVENT)
        elif kind == _BUTTON_EVENT or kind == _BUTTON_RELEASE:
            _on_x_button(ev, kind == _BUTTON_EVENT)
        elif kind == _MOTION_EVENT:
            _on_x_motion(ev)
        drained += 1
        if drained > 4000:
            break
    _flush_remote_move()
    return True


_X_WATCH = {"id": 0}


def _attach_x_watch():
    if _X_WATCH["id"] or not _DPY or not _xcap_ready():
        return
    try:
        fd = _x11.XConnectionNumber(_DPY)
        _X_WATCH["id"] = GLib.io_add_watch(fd, GLib.IO_IN, _on_x_events)
        log("reverse share: X 事件监听已挂上 fd=%d" % fd)
    except Exception as e:
        log("reverse share: 挂 X 事件监听失败 %r" % (e,))


# ---- 全局热键（XGrabKey） ----

def _hotkey_parts():
    spec = str(CONF.get("hotkey") or "ctrl+alt+u").strip()
    parts = [item.strip() for item in spec.split("+") if item.strip()]
    mask = 0
    base = None
    for item in parts:
        low = item.lower()
        if low in ("ctrl", "control"):
            mask |= 4
        elif low == "alt":
            mask |= 8
        elif low == "shift":
            mask |= 1
        elif low in ("super", "win", "meta"):
            mask |= 64
        else:
            base = item
    if base is None:
        return None, mask
    key = KEYMAP.get(base)
    if key is None and len(base) == 1:
        key = base.lower()
    if key is None:
        return None, mask
    keysym = _x11.XStringToKeysym(key.encode("utf-8"))
    if not keysym:
        return None, mask
    code = _x11.XKeysymToKeycode(_DPY, keysym)
    if not code:
        return None, mask
    return int(code), mask


def _unregister_hotkey():
    if not _HOT["code"] or not _DPY:
        _HOT["code"] = 0
        return
    try:
        root = _root_window()
        for extra in (0, 2, 16, 18):
            _x11.XUngrabKey(_DPY, _HOT["code"], _HOT["mask"] | extra, root)
        _x11.XSync(_DPY, 0)
    except Exception:
        pass
    _HOT["code"] = 0


def _register_hotkey():
    _HOT["tried"] = time.time()
    if not CONF.get("reverse_share", True):
        return
    if not _DPY or not _xcap_ready():
        return
    _install_error_handler()
    _unregister_hotkey()
    code, mask = _hotkey_parts()
    if not code:
        log("reverse share: 热键配置无法识别：%r" % (CONF.get("hotkey"),))
        return
    root = _root_window()
    _XERR["codes"] = []
    _XERR["armed"] = True
    try:
        # CapsLock / NumLock 打开时修饰位会变，四种组合都要抓
        for extra in (0, 2, 16, 18):
            _x11.XGrabKey(_DPY, code, mask | extra, root, 0, 1, 1)
        _x11.XSync(_DPY, 0)
    except Exception as e:
        log("reverse share: 注册热键失败 %r" % (e,))
    _XERR["armed"] = False
    if _XERR["codes"]:
        log("reverse share: 热键 %s 被占用（X error %r），换一个 hotkey 再试"
            % (CONF.get("hotkey"), _XERR["codes"]))
        return
    _HOT["code"] = code
    _HOT["mask"] = mask
    log("reverse share: 热键 %s 已注册" % CONF.get("hotkey"))


# ---- 周期检查：等 ok 超时、心跳静默、边缘进入、热键补注册 ----

def _rwin_tick():
    if not CONF.get("reverse_share", True):
        return True
    if _HOT["code"] == 0 and time.time() - _HOT["tried"] > 15:
        if ensure_display():
            _attach_x_watch()
            _register_hotkey()

    if _RWIN["active"]:
        if not _RWIN["confirmed"]:
            if _RWIN["deadline"] and time.time() > _RWIN["deadline"]:
                _leave("对端没有确认", tell_windows=False)
                _tray_notify("反向共享没成功",
                             "Windows 端没有响应，请确认它已升级到 3.7 或更高版本")
        elif time.time() - LAST_RX["t"] > 5.0:
            _leave("心跳静默", tell_windows=False)
            _tray_notify("已放开本机键鼠", "和 Windows 的连接没有心跳了")
        return True

    if not CONNS or not _RWIN["side"] or not CONF.get("edge_entry", True):
        _EDGE["since"] = 0.0
        _LOCAL_MOVE["t"] = 0.0
        return True

    now = time.time()
    if now < _EDGE["cooldown"]:
        _EDGE["since"] = 0.0
        _LOCAL_MOVE["t"] = 0.0
        return True

    pos = _query_pointer()
    if pos is None:
        return True
    x, y, _mask = pos
    px, py = _EDGE["px"], _EDGE["py"]
    _EDGE["px"], _EDGE["py"] = x, y
    idle = now - LAST_INPUT_IN["t"]
    w, h = _RWIN["sw"], _RWIN["sh"]
    if not w or not h:
        w, h = _screen_size()
        _RWIN["sw"], _RWIN["sh"] = w, h
    side = _RWIN["side"]
    moved = px >= 0 and abs(x - px) + abs(y - py) >= 3

    # 允许 2px 误差：某些主题/缩放下落点停在 1 而不是 0，卡死在那里就永远进不去
    at_edge = ((side == "left" and x <= 2) or (side == "right" and x >= w - 3)
               or (side == "top" and y <= 2) or (side == "bottom" and y >= h - 3))

    # 对端正驱动本机：不能按位置判——它注入的指针经常就停在共享边上，
    # 一停就会把控制权抢回来，表现成「刚切到 Ubuntu，碰到左边就弹回去」。
    # 但只要注入停了 0.3 秒，指针再动就只可能是本机有人在操作：记下这个时刻，
    # 之后 0.3 秒内落到边上就接管。这样「快速一甩再按住」也不会漏掉
    # （以前要求连续三拍都在动，甩过去停住就不动了，永远凑不满）。
    if _RWIN["win_owns"]:
        if idle > 0.30 and moved:
            _LOCAL_MOVE["t"] = now
        if idle > 0.30 and at_edge and now - _LOCAL_MOVE["t"] < 0.30:
            _LOCAL_MOVE["t"] = 0.0
            _enter("本机推边缘")
        _EDGE["since"] = 0.0
        return True

    _LOCAL_MOVE["t"] = 0.0

    # 位置判定：指针推到共享边就切，不看推得快慢。
    # 「落到边上」这一拍立刻切——轮询是 60ms，正常推鼠标时上一拍常常还在 40px 外，
    # 所以不能要求上一拍已经很贴边（以前那个 px<=31 的条件让快速推边永远走兜底路径，
    # 用户就得贴着边干等 0.35 秒）。已经在边上停着时只用 0.12 秒去抖。
    arrived = False
    if px >= 0 and py >= 0:
        if side == "left":
            arrived = x <= 2 and px > x
        elif side == "right":
            arrived = x >= w - 3 and px < x
        elif side == "top":
            arrived = y <= 2 and py > y
        else:
            arrived = y >= h - 3 and py < y

    if not at_edge:
        _EDGE["since"] = 0.0
        _EDGE["armed"] = True
        return True

    if not _EDGE["since"]:
        _EDGE["since"] = now
    if not _EDGE["armed"]:
        # 上一次进入失败（对端没连上 / 被拒绝）时指针还压在边上，会一直卡在
        # 「贴边完全没反应」的状态。停够 0.8 秒就重新武装，允许再试一次。
        if now - _EDGE["since"] < 0.8:
            return True
        _EDGE["armed"] = True
    if arrived or now - _EDGE["since"] >= 0.12:
        _EDGE["armed"] = False
        _enter("边缘进入" if arrived else "边缘停留")
    return True


# ---- 托盘 ----

def _tray_toggle_reverse(_item=None):
    if _RWIN["active"]:
        _leave("托盘交还", True)
    elif not CONF.get("reverse_share", True):
        _tray_notify("反向共享已关闭", "在 ~/.config/typebridge/config.json 里把 reverse_share 设为 true")
    else:
        _enter("托盘菜单")



# ---------- 剪贴板双向同步（文本 + 图片） ----------

_clipboard = None
_clipboard_state = {
    "applied": "",          # 刚由 Windows 写进来的文本
    "sent": "",             # 刚推给 Windows 的文本
    "primed": False,        # 启动基线：避免一连上就把旧内容推给对端
    "image_applied": None,  # 刚由 Windows 写进来的图片（PNG 字节）
    "image_sent": None,     # 刚推给 Windows 的图片（PNG 字节）
    "suppress_image": False,
}
_seq = {"clip": 0}


def broadcast(line):
    data = (line + "\n").encode("utf-8")
    sent = 0
    for fd, st in list(CONNS.items()):
        try:
            st["sock"].sendall(data)
            sent += 1
        except OSError:
            # 已经发出半行，留着只会让对端把残帧和下一行拼在一起
            drop(fd, "broadcast failed")
    return sent


def set_clipboard(text):
    """Windows -> Ubuntu：把文本写进本机剪贴板。"""
    if not isinstance(text, str) or not text:
        return
    _clipboard_state["applied"] = text
    try:
        cb = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        cb.set_text(text, -1)
        cb.store()
        log("clipboard <- windows (%d chars)" % len(text))
    except Exception as e:
        log("clipboard set failed: %r" % (e,))


class _GtkTargetEntry(ctypes.Structure):
    _fields_ = [("target", ctypes.c_char_p),
                ("flags", ctypes.c_uint),
                ("info", ctypes.c_uint)]


_GET_FUNC = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_void_p,
                             ctypes.c_uint, ctypes.c_void_p)
_CLEAR_FUNC = ctypes.CFUNCTYPE(None, ctypes.c_void_p, ctypes.c_void_p)
_clip_c = {"ready": False}


def _init_clip_c():
    """PyGObject 没暴露 Gtk.Clipboard.set_with_data（回调要 GClosure），
    这里直接怼 libgtk-3 的 C 接口，才能一次挂上"图片 + 文本"两个目标。"""
    if _clip_c.get("ready"):
        return True
    try:
        gtk = ctypes.CDLL("libgtk-3.so.0")
        gdk = ctypes.CDLL("libgdk-3.so.0")

        gdk.gdk_atom_intern.restype = ctypes.c_void_p
        gdk.gdk_atom_intern.argtypes = [ctypes.c_char_p, ctypes.c_int]

        gtk.gtk_clipboard_get.restype = ctypes.c_void_p
        gtk.gtk_clipboard_get.argtypes = [ctypes.c_void_p]

        gtk.gtk_clipboard_set_with_data.restype = ctypes.c_int
        gtk.gtk_clipboard_set_with_data.argtypes = [
            ctypes.c_void_p, ctypes.POINTER(_GtkTargetEntry), ctypes.c_uint,
            _GET_FUNC, _CLEAR_FUNC, ctypes.c_void_p]

        gtk.gtk_selection_data_set.restype = ctypes.c_int
        gtk.gtk_selection_data_set.argtypes = [
            ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_char_p, ctypes.c_int]

        gtk.gtk_selection_data_set_text.restype = ctypes.c_int
        gtk.gtk_selection_data_set_text.argtypes = [
            ctypes.c_void_p, ctypes.c_char_p, ctypes.c_int]

        _clip_c.update(gtk=gtk, gdk=gdk, ready=True)
        return True
    except Exception as e:
        log("clipboard: 双格式初始化失败 %r" % (e,))
        return False


def _own_image_and_text(png, text):
    """把 image/png 和文本同时放进 CLIPBOARD：粘到图片程序里是图，粘到输入框里是字。"""
    if not _init_clip_c():
        return False
    gtk = _clip_c["gtk"]
    gdk = _clip_c["gdk"]
    try:
        clip = gtk.gtk_clipboard_get(gdk.gdk_atom_intern(b"CLIPBOARD", False))
        if not clip:
            return False
        atom_png = gdk.gdk_atom_intern(b"image/png", False)

        entries = (_GtkTargetEntry * 3)(
            _GtkTargetEntry(b"image/png", 0, 0),
            _GtkTargetEntry(b"text/plain;charset=utf-8", 0, 1),
            _GtkTargetEntry(b"UTF8_STRING", 0, 1),
        )
        png_buffer = ctypes.create_string_buffer(bytes(png), len(png))
        text_buffer = ctypes.create_string_buffer(text.encode("utf-8"))
        png_pointer = ctypes.cast(png_buffer, ctypes.c_char_p)
        text_pointer = ctypes.cast(text_buffer, ctypes.c_char_p)

        def provide(_clip, selection_data, info, _user_data):
            try:
                if info == 0:
                    gtk.gtk_selection_data_set(selection_data, atom_png, 8,
                                               png_pointer, len(png))
                else:
                    gtk.gtk_selection_data_set_text(selection_data, text_pointer, -1)
            except Exception as exc:
                log("clipboard provide failed: %r" % (exc,))

        provide_c = _GET_FUNC(provide)
        clear_c = _CLEAR_FUNC(lambda *_: None)

        ok = gtk.gtk_clipboard_set_with_data(clip, entries, 3, provide_c, clear_c, None)
        if not ok:
            return False
        # 这些对象必须一直活着，否则回调指针悬空会让 GTK 崩
        _clip_c["keep"] = (entries, png_buffer, text_buffer, provide_c, clear_c, provide)
        return True
    except Exception as e:
        log("clipboard: 双格式设置失败 %r" % (e,))
        return False


def set_clipboard_image(data, text=None):
    """Windows -> Ubuntu：把图片（可带文本）写进本机剪贴板（必须在主循环线程里调用）。"""
    if not data:
        return
    _clipboard_state["image_applied"] = data
    if text:
        _clipboard_state["applied"] = text
    # 自己成为剪贴板 owner 后 GTK 会再发一次 owner-change，回读出来重新编码的
    # PNG 跟原图不一定逐字节相同，所以这里用标志把回声拦掉。
    _clipboard_state["suppress_image"] = True
    try:
        loader = GdkPixbuf.PixbufLoader()
        loader.write(data)
        loader.close()
        pixbuf = loader.get_pixbuf()
    except Exception as e:
        log("clipboard image decode failed: %r" % (e,))
        return
    if pixbuf is None:
        log("clipboard image decode failed: empty pixbuf")
        return
    try:
        cb = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        if text:
            if not _own_image_and_text(data, text):
                # 双格式挂不上就退回纯文本：宁可文字能粘，
                # 也不要让剪贴板变成一个什么都粘不出来的状态
                cb.set_text(text, -1)
                cb.store()
                log("clipboard: 双格式不可用，退回纯文本")
                return
        else:
            cb.set_image(pixbuf)
            cb.store()
        log("clipboard <- windows image (%dx%d, %d bytes, text %d chars)"
            % (pixbuf.get_width(), pixbuf.get_height(), len(data), len(text or "")))
    except Exception as e:
        log("clipboard image set failed: %r" % (e,))


def push_clipboard_image(data):
    """Ubuntu -> Windows：走文件通道把 PNG 推过去，由 Windows 端写进剪贴板。"""
    if not data:
        return
    peer = LAST_PEER["addr"]
    if not peer:
        log("clipboard image: 还没有 Windows 连进来")
        return
    port = int(CONF.get("file_recv_port", 4323))
    sock = None
    try:
        sock = socket.create_connection((peer, port), 5)
        sock.sendall(("CLIPIMG %d 0\n" % len(data)).encode("utf-8"))
        sock.sendall(data)
        reply = sock.makefile("rb").readline(4096).decode("utf-8", "replace").strip()
        log("clipboard image -> windows: %s" % (reply or "(no reply)"))
    except Exception as e:
        log("clipboard image push failed: %r" % (e,))
    finally:
        if sock is not None:
            try:
                sock.close()
            except OSError:
                pass


def _clipboard_image_seen(pixbuf):
    try:
        ok, data = pixbuf.save_to_bufferv("png", [], [])
    except Exception as e:
        log("clipboard image encode failed: %r" % (e,))
        return
    if not ok or not data:
        return
    if _clipboard_state["suppress_image"]:
        _clipboard_state["suppress_image"] = False
        _clipboard_state["image_sent"] = data
        return
    if not _clipboard_state["primed"]:
        _clipboard_state["primed"] = True
        _clipboard_state["image_sent"] = data
        return
    if data == _clipboard_state["image_sent"] or data == _clipboard_state["image_applied"]:
        return
    _clipboard_state["image_sent"] = data
    log("clipboard image -> windows (%d bytes)" % len(data))
    threading.Thread(target=push_clipboard_image, args=(data,), daemon=True).start()


def _clipboard_image_ready(clipboard, pixbuf, seq):
    if seq != _seq["clip"]:
        return
    if pixbuf is not None:
        _clipboard_image_seen(pixbuf)
        return
    # 这次复制不是图片，退回文本
    try:
        clipboard.request_text(_clipboard_text_ready, seq)
    except Exception as e:
        log("clipboard request failed: %r" % (e,))


def _clipboard_text_ready(clipboard, text, user_data):
    seq = user_data
    if seq is not None and seq != _seq["clip"]:
        return
    if not text:
        return
    if not _clipboard_state["primed"]:
        # 启动时先记下现有内容当基线，避免连上就把旧内容推给客户端
        _clipboard_state["primed"] = True
        _clipboard_state["sent"] = text
        return
    if text == _clipboard_state["applied"] or text == _clipboard_state["sent"]:
        return
    _clipboard_state["sent"] = text
    _clipboard_state["image_sent"] = None
    log("clipboard -> windows (%d chars)" % len(text))
    broadcast("C " + json.dumps(text))


def _on_clipboard_owner_change(clipboard, event, user_data=None):
    _seq["clip"] += 1
    seq = _seq["clip"]
    # 先问图片：浏览器/截图复制出来的内容常常同时带文本和位图，
    # 图片优先才符合"复制了张图"的直觉。
    try:
        clipboard.request_image(_clipboard_image_ready, seq)
    except Exception as e:
        log("clipboard image request failed: %r" % (e,))
        try:
            clipboard.request_text(_clipboard_text_ready, seq)
        except Exception:
            pass


def watch_clipboard():
    global _clipboard
    try:
        _clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        _clipboard.connect("owner-change", _on_clipboard_owner_change)
        # 先取一次当前内容当基线，之后只在真正变化时才推送
        _clipboard.request_image(_clipboard_image_ready, _seq["clip"])
        log("clipboard watch enabled (text + image)")
    except Exception as e:
        _clipboard = None
        log("clipboard watch failed: %r" % (e,))


# ---------- 托盘指示器：后台跑着也要看得见、点得到 ----------

_tray = {"indicator": None, "fallback": False}


def _tray_icon_name():
    try:
        if Gtk.IconTheme.get_default().has_icon("typebridge"):
            return "typebridge"
    except Exception:
        pass
    # 没装图标包时用主题自带图标：连着用键盘图标，没连用离线图标
    return "input-keyboard-symbolic" if CONNS else "network-offline-symbolic"


def _tray_tooltip():
    if _RWIN["active"]:
        return "TypeBridge：正在用本机键鼠控制 Windows"
    return "TypeBridge：Windows 已连接" if CONNS else "TypeBridge：等待 Windows 连接"


def _tray_notify(title, body):
    try:
        subprocess.Popen(["notify-send", "-a", "TypeBridge", "-i", "input-keyboard", title, body],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except Exception:
        log("%s: %s" % (title, body))


def _systemctl_user(action):
    try:
        subprocess.Popen(["systemctl", "--user", action, "typebridge"],
                         stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        log("tray: systemctl --user %s typebridge" % action)
    except Exception as e:
        log("tray: systemctl failed: %r" % (e,))


def settings_command():
    """设置页可能在 ~/.local/bin（源码安装），也可能在 /usr/bin（deb 安装）。"""
    for candidate in ("typebridge-ui", "/usr/bin/typebridge-ui",
                      os.path.expanduser("~/.local/bin/typebridge-ui"),
                      os.path.expanduser("~/.local/bin/typebridge-ui.py"),
                      "/usr/lib/typebridge/typebridge-ui.py"):
        found = candidate if os.path.isabs(candidate) else shutil.which(candidate)
        if found and os.path.exists(found):
            if found.endswith(".py"):
                return ["/usr/bin/python3", found]
            return [found]
    return None


def _tray_open_settings(_item=None):
    cmd = settings_command()
    if not cmd:
        log("tray: 找不到设置页")
        _tray_notify("没有找到设置页", "请把 typebridge-ui 放到 PATH 里，或安装 deb 包")
        return
    try:
        subprocess.Popen(cmd, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                         start_new_session=True)
        log("tray: 打开设置 %s" % " ".join(cmd))
    except Exception as e:
        log("tray: 打开设置失败: %r" % (e,))


def _tray_switch_back(_item=None):
    sent = broadcast("BT")
    log("tray: 请求切回 Windows（发给 %d 个连接）" % sent)
    if not sent:
        _tray_notify("还没连上 Windows", "等 Windows 端连上以后再点")


def _build_tray_menu():
    menu = Gtk.Menu()

    status = Gtk.MenuItem(label="Windows 已连接" if CONNS else "等待 Windows 连接")
    status.set_sensitive(False)
    menu.append(status)

    switch = Gtk.MenuItem(label="切回 Windows")
    switch.set_sensitive(bool(CONNS))
    switch.connect("activate", _tray_switch_back)
    menu.append(switch)

    reverse = Gtk.MenuItem(label="交还键鼠给本机" if _RWIN["active"] else "用本机键鼠控制 Windows")
    reverse.set_sensitive(bool(CONNS) or _RWIN["active"])
    reverse.connect("activate", _tray_toggle_reverse)
    menu.append(reverse)

    menu.append(Gtk.SeparatorMenuItem())

    settings = Gtk.MenuItem(label="打开设置…")
    settings.connect("activate", _tray_open_settings)
    menu.append(settings)

    restart = Gtk.MenuItem(label="重启服务")
    restart.connect("activate", lambda *_: _systemctl_user("restart"))
    menu.append(restart)

    stop = Gtk.MenuItem(label="停止服务")
    stop.connect("activate", lambda *_: _systemctl_user("stop"))
    menu.append(stop)

    menu.show_all()
    return menu


def _on_status_icon_popup(icon, button, activate_time):
    # 回退模式（Gtk.StatusIcon）每次点开都重建菜单，状态才是新的
    menu = _build_tray_menu()
    menu.popup(None, None, Gtk.StatusIcon.position_menu, icon, button, activate_time)


def start_tray():
    """在桌面顶栏放一个 TypeBridge 标志；没有托盘宿主就只记日志，不影响转发。"""
    menu = _build_tray_menu()

    for module_name in ("AyatanaAppIndicator3", "AppIndicator3"):
        try:
            gi.require_version(module_name, "0.1")
            module = __import__("gi.repository", fromlist=[module_name])
            api = getattr(module, module_name)
            indicator = api.Indicator.new("typebridge", _tray_icon_name(),
                                          api.IndicatorCategory.APPLICATION_STATUS)
            indicator.set_status(api.IndicatorStatus.ACTIVE)
            indicator.set_title("TypeBridge")
            indicator.set_menu(menu)
            _tray["indicator"] = indicator
            _tray["fallback"] = False
            log("tray: 已用 %s 显示顶栏图标" % module_name)
            return
        except Exception as e:
            log("tray: %s 不可用（%r）" % (module_name, e))

    try:
        icon = Gtk.StatusIcon()
        icon.set_from_icon_name(_tray_icon_name())
        icon.set_tooltip_text(_tray_tooltip())
        icon.connect("popup-menu", _on_status_icon_popup)
        icon.set_visible(True)
        _tray["indicator"] = icon
        _tray["fallback"] = True
        log("tray: 回退到 Gtk.StatusIcon")
    except Exception as e:
        _tray["indicator"] = None
        log("tray: 没有可用的托盘宿主（%r），只在日志里报告状态" % (e,))


def refresh_tray():
    indicator = _tray["indicator"]
    if indicator is None:
        return
    try:
        if _tray["fallback"]:
            indicator.set_from_icon_name(_tray_icon_name())
            indicator.set_tooltip_text(_tray_tooltip())
        else:
            indicator.set_icon_full(_tray_icon_name(), _tray_tooltip())
            indicator.set_menu(_build_tray_menu())
    except Exception as e:
        log("tray refresh failed: %r" % (e,))


def handle_line(line, sock):
    """返回 False 表示对端请求断开(QUIT)"""
    if line == "PING":
        try:
            sock.sendall(b"PONG\n")
        except OSError:
            pass
        return True
    if line == "QUIT":
        return False
    if line.startswith("DIR "):
        target = line[4:].strip()
        # 空值表示「用默认桌面」：客户端 UI 是唯一事实来源，不能沿用上次的旧路径
        if target and not valid_file_dir(target):
            log("DIR rejected: %r" % target[:120])
            return True
        CONF["file_dir"] = target
        try:
            os.makedirs(resolve_file_dir(), exist_ok=True)
        except OSError as e:
            log("file_dir mkdir failed: %r" % (e,))
        try:
            with open(CONF_FILE, "w", encoding="utf-8") as fh:
                json.dump(CONF, fh, ensure_ascii=False, indent=2)
        except Exception:
            pass
        log("file_dir -> %s" % resolve_file_dir())
        try:
            sock.sendall(("DIR " + resolve_file_dir() + "\n").encode("utf-8"))
        except OSError:
            pass
        return True
    if line == "PEER":
        # 拖放区要问：最近连进来的 Windows 是哪台。
        # 调用者自己是 127.0.0.1，必须用记录下来的真实客户端地址。
        addr = LAST_PEER["addr"]
        if not addr:
            st = CONNS.get(sock.fileno())
            addr = st["addr"][0] if st else "0.0.0.0"
        try:
            sock.sendall(("PEER " + addr + "\n").encode("utf-8"))
        except OSError:
            pass
        return True
    if line == "WHERE":
        # Windows 端问：文件到底会落到哪个目录
        try:
            sock.sendall(("DIR " + resolve_file_dir() + "\n").encode("utf-8"))
        except OSError:
            pass
        return True
    if line.startswith("CUR "):
        # Windows 说：这台 Ubuntu 的指针该不该显示。
        # 3.7.3 起不再因为「控制权在 Windows」就把本机指针长期藏起来：
        # 人走到 Ubuntu 前面时鼠标是隐形的，桌面根本没法用（实测踩到）。
        # 只有在反向接管期间（我们自己抓取键鼠）才允许隐藏。
        if not _RWIN["active"]:
            set_pointer_visible(True, force=True)
        return True
    if line.startswith("OWN "):
        # Windows 说：它是不是正在把键鼠送到这台机器。是的话不能反向接管，否则成环路
        _RWIN["win_owns"] = line[4:].strip() != "0"
        # 归属一变就先撤掉边缘武装：刚交还时指针还压在共享边上，
        # 不撤的话半秒后就会被判成"又推了一次"，来回来回弹。
        _EDGE["armed"] = False
        _EDGE["since"] = 0.0
        return True
    if line.startswith("SW "):
        # Windows 报告自己的屏幕尺寸和相对方位：边缘进入要靠它判定推哪条边
        parts = line.split()
        if len(parts) >= 4:
            try:
                _RWIN["win_w"] = int(parts[1])
                _RWIN["win_h"] = int(parts[2])
            except ValueError:
                pass
            side = parts[3].strip().lower()
            if side in ("left", "right", "top", "bottom"):
                _RWIN["side"] = side
            log("reverse share: Windows 在%s侧 %dx%d"
                % (side, _RWIN["win_w"], _RWIN["win_h"]))
        return True
    if line.startswith("RGW"):
        arg = line[3:].strip()
        if arg == "ok":
            if _RWIN["active"]:
                _RWIN["confirmed"] = True
                log("reverse share: Windows 已确认接管")
        elif arg == "0":
            if _RWIN["active"]:
                # 别再弹桌面通知：边缘切换是高频操作，每切一次弹一个气泡很碍事，
                # 托盘图标和指针本身已经足够说明控制权在哪边。
                _leave("Windows 收回", tell_windows=False)
        elif arg.startswith("no"):
            if _RWIN["active"]:
                _leave("Windows 拒绝", tell_windows=False)
                reason = arg[2:].strip()
                if reason == "disabled":
                    _tray_notify("切不过去", "Windows 端的设置里关掉了「允许 Ubuntu 反向控制」")
                elif reason == "busy":
                    _tray_notify("切不过去", "这台 Windows 正把键鼠送往 Ubuntu，先收回再试")
                else:
                    _tray_notify("切不过去", "Windows 端拒绝了这次接管")
        return True
    if line.startswith("RP "):
        # Windows 交还时把本机指针放到共享边内侧，接着就能继续用
        parts = line.split()
        if len(parts) == 3:
            try:
                x, y = int(parts[1]), int(parts[2])
            except ValueError:
                return True
            w, h = _RWIN["sw"], _RWIN["sh"]
            if w and h:
                x = max(0, min(w - 1, x))
                y = max(0, min(h - 1, y))
            _warp(x, y)
            _RWIN["last_x"], _RWIN["last_y"] = x, y
            _RWIN["ignore_motion"] = 1
        return True
    if line == "OPEN":
        # Windows 端托盘菜单「在 Ubuntu 上打开接收目录」
        directory = resolve_file_dir()
        try:
            os.makedirs(directory, exist_ok=True)
            subprocess.Popen(["xdg-open", directory],
                             stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                             start_new_session=True)
            log("open receive dir: %s" % directory)
            reply = "OPEN ok"
        except Exception as e:
            log("open receive dir failed: %r" % (e,))
            reply = "OPEN %s" % (e,)
        try:
            sock.sendall((reply + "\n").encode("utf-8"))
        except OSError:
            pass
        return True
    if len(line) < 3 or line[1] != " ":
        return True
    cmd, payload = line[0], line[2:]
    if cmd in ("K", "M", "B", "W", "P"):
        # 记下"对端正在注入"的时间，边缘判定据此区分本地人手和对端驱动
        LAST_INPUT_IN["t"] = time.time()
    if cmd == "T":
        try:
            text = json.loads(payload)
            if isinstance(text, str) and text:
                inject_text(text)
        except Exception as e:
            log("bad T payload: %r" % (e,))
    elif cmd == "K":
        key = normalize_key(payload)
        if key:
            inject_key(key)
        else:
            log("unsupported K payload: %r" % payload)
    elif cmd == "M":
        mouse_move(payload)
    elif cmd == "B":
        mouse_button(payload)
    elif cmd == "W":
        mouse_wheel(payload)
    elif cmd == "P":
        pointer_move_abs(payload)
    elif cmd == "C":
        try:
            text = json.loads(payload)
        except Exception as e:
            log("bad C payload: %r" % (e,))
            return True
        set_clipboard(text)
    return True


def process_buffer(st):
    sock, buf = st["sock"], st["buf"]
    while b"\n" in buf:
        raw, buf = buf.split(b"\n", 1)
        line = raw.decode("utf-8", "replace").rstrip("\r")
        # 非位移指令前先把积攒的位移落下去，保证"先移动后点击"的顺序
        if line[:1] != "M":
            flush_move()
        try:
            if not handle_line(line, sock):
                st["buf"] = buf
                flush_move()
                return False
        except Exception as e:
            log("handle error: %r" % (e,))
    st["buf"] = buf
    flush_move()
    return True


def on_accept(fd, cond):
    try:
        conn, addr = srv.accept()
    except (BlockingIOError, OSError):
        return True
    if not addr[0].startswith("127."):
        LAST_PEER["addr"] = addr[0]
    # 关掉 Nagle：心跳和按键都是小包，等延迟 ACK 会让 RTT 虚高 40ms，
    # 输入也会发黏
    try:
        conn.setsockopt(socket.IPPROTO_TCP, socket.TCP_NODELAY, 1)
    except OSError:
        pass
    # 先告诉客户端远端屏幕尺寸，边缘切屏要用它算坐标
    try:
        screen = Gdk.Screen.get_default()
        conn.sendall(("S %d %d\n" % (screen.get_width(), screen.get_height())).encode("utf-8"))
    except Exception as e:
        log("screen size send failed: %r" % (e,))
    conn.setblocking(False)
    st = {"sock": conn, "addr": addr, "buf": b""}
    CONNS[conn.fileno()] = st
    refresh_tray()
    log("client connected: %s:%s" % (addr[0], addr[1]))
    src = GLib.io_add_watch(conn.fileno(),
                            GLib.IO_IN | GLib.IO_HUP | GLib.IO_ERR,
                            on_data, conn, addr)
    log("data watch fd=%d" % conn.fileno())
    return True


def on_data(fd, cond, conn, addr):
    st = CONNS.get(fd)
    if st is None:
        return False
    if cond & (GLib.IO_HUP | GLib.IO_ERR):
        return drop(fd, "closed: %s" % addr[0])
    try:
        data = conn.recv(4096)
    except (BlockingIOError, socket.timeout):
        return True
    except OSError:
        return drop(fd, "recv error")
    if not data:
        return drop(fd, "client closed: %s" % addr[0])
    st["buf"] += data
    # 反向共享靠这条时间戳判断链路是不是还活着（Windows 每秒发 PING）
    LAST_RX["t"] = time.time()
    if len(st["buf"]) > 1 << 20:
        return drop(fd, "buffer overflow")
    if not process_buffer(st):
        return drop(fd, "quit: %s" % addr[0])
    return True


def drop(fd, msg):
    st = CONNS.pop(fd, None)
    if st:
        try:
            st["sock"].close()
        except OSError:
            pass
        if st.get("out_watch"):
            try:
                GLib.source_remove(st["out_watch"])
            except Exception:
                pass
            st["out_watch"] = 0
        # 正在用这台机器驱动 Windows 时链路断了：必须立刻放开抓取，
        # 否则用户会被锁在自己的桌面外面（X 只在进程退出时才自动释放）
        if _RWIN["active"] and _RWIN["sock"] is st["sock"]:
            _leave("连接断开", tell_windows=False)
        release_mouse_buttons("disconnect")
        # 客户端走了，没人再控制这台机器，指针必须恢复可见
        set_pointer_visible(True)
        refresh_tray()
    log(msg)
    return False


srv = None


# ---------- 文件接收（独立端口：一行头 + 原始字节） ----------

FILE_MAX_NAME = 180
CLIPIMG_MAX_BYTES = 64 << 20      # 剪贴板图片只进内存，必须有上限
CLIPIMG_MAX_TEXT = 1 << 20


def safe_name(name):
    """只保留文件名本体，去掉路径和危险字符，避免写到别处。"""
    name = os.path.basename((name or "").replace("\\", "/")).strip()
    name = re.sub(r'[\x00-\x1f<>:"/\\|?*]', "_", name)
    if name in ("", ".", ".."):
        name = "file"
    if len(name) > FILE_MAX_NAME:
        root, ext = os.path.splitext(name)
        name = root[:FILE_MAX_NAME - len(ext)] + ext
    return name


def unique_path(directory, name):
    """同名只保留一份：直接覆盖，不再生成 (1)、(2) 副本。"""
    return os.path.join(directory, name)


def valid_file_dir(target):
    """拒绝路径穿越和系统目录：控制端口没有认证，DIR 是可被任意对端改写的持久化配置。"""
    if ".." in target.replace("\\", "/").split("/"):
        return False
    path = os.path.abspath(os.path.expanduser(target))
    blocked = ("/etc", "/usr", "/bin", "/sbin", "/lib", "/proc", "/sys",
               "/dev", "/boot", "/root", "/var", "/opt")
    for prefix in blocked:
        if path == prefix or path.startswith(prefix + os.sep):
            return False
    return True


def resolve_file_dir(name=""):
    """默认直接落到桌面：不装任何 Ubuntu 端程序也能看到文件。"""
    configured = str(CONF.get("file_dir") or "").strip()
    if configured:
        return os.path.expanduser(configured)
    try:
        desktop = GLib.get_user_special_dir(GLib.UserDirectory.DIRECTORY_DESKTOP)
        if desktop:
            return desktop
    except Exception:
        pass
    for candidate in ("~/Desktop", "~/桌面", "~/Downloads"):
        path = os.path.expanduser(candidate)
        if os.path.isdir(path):
            return path
    return os.path.expanduser("~/Downloads")


def part_meta_path(dest):
    return dest + ".part.info"


_PART_LOCKS = {}
_PART_LOCKS_GUARD = threading.Lock()


def part_lock_for(dest):
    with _PART_LOCKS_GUARD:
        lock = _PART_LOCKS.get(dest)
        if lock is None:
            lock = threading.Lock()
            _PART_LOCKS[dest] = lock
        return lock


def resume_offset(dest, name, size):
    """已经收到多少字节可以接着传。文件名或总大小对不上就返回 0，绝不能拼错文件。"""
    part = dest + ".part"
    try:
        with open(part_meta_path(dest), "r", encoding="utf-8") as fh:
            meta = json.load(fh)
    except Exception:
        return 0
    if meta.get("name") != name or meta.get("size") != size:
        return 0
    try:
        have = os.path.getsize(part)
    except OSError:
        return 0
    if have <= 0 or have >= size:
        return 0
    return have


def handle_upload(conn, addr):
    try:
        conn.settimeout(120)  # 空闲 120s 才判死：大文件+慢链路别动不动断
        stream = conn.makefile("rb")
        header = stream.readline(4096).decode("utf-8", "replace").strip()
        parts = header.split(" ", 2)
        if len(parts) != 3 or parts[0] not in ("UPLOAD", "CLIPIMG", "RESUME"):
            log("file: bad header %r from %s" % (header[:60], addr[0]))
            conn.sendall(b"ERR bad header\n")
            return
        try:
            size = int(parts[1])
        except ValueError:
            conn.sendall(b"ERR bad size\n")
            return
        if size < 0:
            conn.sendall(b"ERR bad size\n")
            return

        if parts[0] == "RESUME":
            # 断点续传握手：告诉发送方 .part 里已经有几个字节。
            # 头是 "RESUME <总大小> <文件名>"，第三段整体就是文件名、没有偏移字段；
            # 原来按空格 rpartition 会把 "IMG 2" 截成 "IMG"，与 UPLOAD 分支不一致。
            name = safe_name(parts[2])
            dest = unique_path(resolve_file_dir(name), name)
            with part_lock_for(dest):
                have = resume_offset(dest, name, size)
            log("resume query: %s have %d/%d" % (name, have, size))
            conn.sendall(("HAVE %d\n" % have).encode("utf-8"))
            return

        if parts[0] == "CLIPIMG":
            if size > CLIPIMG_MAX_BYTES:
                conn.sendall(b"ERR clipboard too large\n")
                return
            # 剪贴板图片：只进内存，不落盘。
            # 第三段是可选文本长度；旧版客户端这里放的是文件名，解析失败按 0 处理。
            try:
                caption_len = int(parts[2])
            except ValueError:
                caption_len = 0
            if caption_len < 0:
                caption_len = 0
            if caption_len > CLIPIMG_MAX_TEXT:
                conn.sendall(b"ERR clipboard too large\n")
                return

            data = bytearray()
            while len(data) < size:
                chunk = stream.read(min(1 << 16, size - len(data)))
                if not chunk:
                    break
                data += chunk
            if len(data) != size:
                log("clipboard image: incomplete (%d/%d) from %s" % (len(data), size, addr[0]))
                conn.sendall(b"ERR incomplete\n")
                return

            caption = bytearray()
            while len(caption) < caption_len:
                chunk = stream.read(min(1 << 16, caption_len - len(caption)))
                if not chunk:
                    break
                caption += chunk
            text = caption.decode("utf-8", "replace")

            # GTK 剪贴板只能在主循环线程里操作，这里排队过去
            GLib.idle_add(set_clipboard_image, bytes(data), text)
            conn.sendall(b"OK clipboard\n")
            return

        # 第四段是可选的续传偏移。文件名本身可能以「空格 + 数字」结尾（如 IMG 2），
        # 只有确实存在匹配的断点记录时才把尾段当偏移，否则整段都当文件名。
        raw = parts[2]
        name, _, tail = raw.rpartition(" ")
        offset = 0
        if tail.isdigit():
            candidate = safe_name(name)
            candidate_dest = unique_path(resolve_file_dir(candidate), candidate)
            if resume_offset(candidate_dest, candidate, size) == int(tail):
                offset = int(tail)
                name = candidate
            else:
                name = raw
        else:
            name = raw
        name = safe_name(name)
        directory = resolve_file_dir(name)
        os.makedirs(directory, exist_ok=True)
        dest = unique_path(directory, name)
        temp_dest = dest + ".part"

        with part_lock_for(dest):
            if offset > 0:
                try:
                    have = os.path.getsize(temp_dest)
                except OSError:
                    have = 0
                if have < offset or offset > size:
                    log("file: %s 断点 %d 与本地 %d 不符，改为从头传" % (name, offset, have))
                    offset = 0

            if offset > 0:
                log("file: %s 从 %d/%d (%d%%) 续传"
                    % (name, offset, size, offset * 100 // max(1, size)))

            started = time.time()
            received = offset
            last_mark = offset
            mode = "r+b" if offset > 0 else "wb"
            with open(temp_dest, mode) as out:
                if offset > 0:
                    out.seek(offset)
                    out.truncate(offset)
                else:
                    try:
                        with open(part_meta_path(dest), "w", encoding="utf-8") as meta:
                            json.dump({"name": name, "size": size}, meta)
                    except OSError:
                        pass
                while received < size:
                    chunk = stream.read(min(1 << 18, size - received))
                    if not chunk:
                        break
                    out.write(chunk)
                    received += len(chunk)
                    # 大文件按 25% 打点，出问题能看出断在第几段
                    if size >= (64 << 20) and received - last_mark >= size // 4:
                        last_mark = received
                        log("file: %s %d%% (%d/%d MB)"
                            % (name, received * 100 // size, received >> 20, size >> 20))

            if received != size:
                # 保留 .part 和 .part.info：下次同一个文件重发就能接着传
                log("file: incomplete %s (%d/%d) from %s，断点已保留"
                    % (name, received, size, addr[0]))
                conn.sendall(b"ERR incomplete\n")
                return

            # 原子替换：要么是完整的旧文件，要么是完整的新文件，不会留下半截或副本
            os.replace(temp_dest, dest)
            try:
                os.remove(part_meta_path(dest))
            except OSError:
                pass

            elapsed = max(0.001, time.time() - started)
            log("file <- %s (%d bytes, 本次 %d bytes, %.1f MB/s%s) -> %s"
                % (name, size, size - offset, (size - offset) / elapsed / 1048576.0,
                   "，续传" if offset else "", dest))
            # 桌面提醒：接收方也要知道文件落在哪个目录，不然只能自己翻
            _tray_notify("收到文件", "%s\n→ %s" % (name, os.path.dirname(dest)))
            conn.sendall(("OK " + dest + "\n").encode("utf-8"))
    except Exception as e:
        log("file upload failed: %r" % (e,))
        try:
            conn.sendall(b"ERR exception\n")
        except OSError:
            pass
    finally:
        try:
            conn.close()
        except OSError:
            pass


def file_accept_loop(sock):
    while True:
        try:
            conn, addr = sock.accept()
        except OSError:
            return
        threading.Thread(target=handle_upload, args=(conn, addr), daemon=True).start()


# ---------- 桌面出件箱：丢进文件夹就传到 Windows，无需额外程序 ----------

OUTBOX_NAME = "发送到Windows"


def desktop_dir():
    """用户桌面目录（XDG），出件箱固定放这里，和 file_dir 无关。"""
    try:
        desktop = GLib.get_user_special_dir(GLib.UserDirectory.DIRECTORY_DESKTOP)
        if desktop:
            return desktop
    except Exception:
        pass
    for candidate in ("~/Desktop", "~/桌面"):
        path = os.path.expanduser(candidate)
        if os.path.isdir(path):
            return path
    return os.path.expanduser("~")


def outbox_dir():
    return os.path.join(desktop_dir(), OUTBOX_NAME)


def push_file(path, host, port):
    size = os.path.getsize(path)
    name = os.path.basename(path)
    offset = 0

    # 先问对端已有多少字节；旧版接收端回 ERR bad header 时按从头传走老协议。
    # 与 Windows -> Ubuntu 方向用同一套 HAVE / UPLOAD ... <offset> 约定。
    try:
        probe = socket.create_connection((host, port), 5)
        try:
            probe.settimeout(5)
            probe.sendall(("RESUME %d %s\n" % (size, name)).encode("utf-8"))
            answer = probe.makefile("rb").readline(4096).decode("utf-8", "replace").strip()
            if answer.startswith("HAVE "):
                have = int(answer[5:].strip())
                if 0 < have < size:
                    offset = have
        finally:
            probe.close()
    except Exception as e:
        log("outbox resume probe failed: %r" % (e,))

    sock = socket.create_connection((host, port), 5)
    try:
        # 只有 connect 超时不够：对端僵住时 sendall / readline 会永久占住 in-flight
        sock.settimeout(60)
        header = "UPLOAD %d %s" % (size, name)
        if offset:
            header += " %d" % offset
        sock.sendall((header + "\n").encode("utf-8"))
        with open(path, "rb") as source:
            if offset:
                source.seek(offset)
            while True:
                chunk = source.read(1 << 16)
                if not chunk:
                    break
                sock.sendall(chunk)
        return sock.makefile("rb").readline(4096).decode("utf-8", "replace").strip()
    finally:
        try:
            sock.close()
        except OSError:
            pass


OUTBOX_INFLIGHT = set()
OUTBOX_GUARD = threading.Lock()
OUTBOX_NEXT_TRY = {}


def outbox_worker(path):
    try:
        peer = LAST_PEER["addr"]
        if not peer:
            return
        port = int(CONF.get("file_recv_port", 4323))
        try:
            reply = push_file(path, peer, port)
            if reply.startswith("OK"):
                done = os.path.join(os.path.dirname(path), "已发送")
                os.makedirs(done, exist_ok=True)
                os.replace(path, os.path.join(done, os.path.basename(path)))
                with OUTBOX_GUARD:
                    OUTBOX_NEXT_TRY.pop(path, None)
                log("outbox -> %s: %s" % (peer, os.path.basename(path)))
            else:
                raise RuntimeError(reply or "no reply")
        except Exception as e:
            # 连不上 Windows 时不要每 2 秒重试 + 刷日志，退避 30 秒
            with OUTBOX_GUARD:
                now = time.time()
                if OUTBOX_NEXT_TRY.get(path, 0) <= now:
                    log("outbox failed (retry in 30s): %s: %r" % (os.path.basename(path), e))
                OUTBOX_NEXT_TRY[path] = now + 30
    finally:
        with OUTBOX_GUARD:
            OUTBOX_INFLIGHT.discard(path)


def scan_outbox():
    try:
        directory = outbox_dir()
        if not os.path.isdir(directory):
            os.makedirs(directory, exist_ok=True)
        if os.path.isdir(directory):
            now = time.time()
            for name in sorted(os.listdir(directory)):
                path = os.path.join(directory, name)
                if not os.path.isfile(path):
                    continue
                # 没有 in-flight 集合时，一个传得慢的大文件会被每 2 秒重复起线程并发重发
                with OUTBOX_GUARD:
                    if path in OUTBOX_INFLIGHT or OUTBOX_NEXT_TRY.get(path, 0) > now:
                        continue
                    OUTBOX_INFLIGHT.add(path)
                threading.Thread(target=outbox_worker, args=(path,), daemon=True).start()
    except Exception as e:
        log("outbox scan error: %r" % (e,))
    return True


def start_file_server():
    port = int(CONF.get("file_port", 4322) or 0)
    if port <= 0:
        log("file server disabled")
        return
    directory = resolve_file_dir()
    sock = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    sock.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        sock.bind((str(CONF.get("bind", "0.0.0.0")), port))
    except OSError as e:
        log("文件端口 %d 绑定失败: %s" % (port, e))
        return
    sock.listen(4)
    threading.Thread(target=file_accept_loop, args=(sock,), daemon=True).start()
    log("file server on %s:%d -> %s" % (CONF.get("bind"), port, directory))


def main():
    if not pick_display():
        log("没有可用的图形会话（可能在登录界面），退出等 systemd 重试")
        sys.exit(3)
    global CONF, ROUTES, srv
    CONF.update(load_json(CONF_FILE, {}))
    ROUTES = load_json(ROUTES_FILE, {})
    os.makedirs(CONF_DIR, exist_ok=True)

    bind = CONF.get("bind", "0.0.0.0")
    port = int(CONF.get("port", 4321))
    srv = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    srv.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    try:
        srv.bind((bind, port))
    except OSError as e:
        log("端口 %d 被占用: %s" % (port, e))
        raise SystemExit(1)
    srv.listen(4)
    srv.setblocking(False)
    GLib.io_add_watch(srv.fileno(), GLib.IO_IN, on_accept)
    watch_clipboard()
    start_file_server()
    start_tray()
    # 上一个进程可能把指针藏起来了（XFixes 的隐藏状态在服务端窗口上），启动先还原
    set_pointer_visible(True, force=True)
    # 反向共享：确保显示连接可用，再注册全局热键、挂 X 事件监听
    try:
        if ensure_display():
            _attach_x_watch()
            _register_hotkey()
        GLib.timeout_add(60, _rwin_tick)
        log("reverse share: hotkey=%s edge_entry=%s share=%s"
            % (CONF.get("hotkey"), CONF.get("edge_entry", True),
               CONF.get("reverse_share", True)))
    except Exception as e:
        log("reverse share setup failed: %r" % (e,))
    try:
        os.makedirs(outbox_dir(), exist_ok=True)
        GLib.timeout_add_seconds(2, scan_outbox)
        log("outbox watching: " + outbox_dir())
    except Exception as e:
        log("outbox setup failed: %r" % (e,))
    log("listening on %s:%d" % (bind, port))
    GLib.MainLoop().run()


if __name__ == "__main__":
    main()
