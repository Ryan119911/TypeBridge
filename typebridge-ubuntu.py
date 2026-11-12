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
import threading
import time

faulthandler.enable()

# ---- 必须在任何 Gtk/Gdk 调用之前选定 DISPLAY ----
# Gtk.Clipboard.get() 在模块导入阶段就会初始化 GTK；写死 :0 会让整个 GUI 栈失效。
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
    """列出可能的显示：先环境变量，再从 /tmp/.X11-unix 里按时间新的优先。"""
    found = []
    env = os.environ.get("DISPLAY", "")
    if env:
        found.append(env)
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


def set_pointer_visible(visible, force=False):
    global _DPY
    if not _DPY:
        return
    lib = _load_xfixes()
    if not lib:
        return
    try:
        root = _x11.XDefaultRootWindow(_DPY)
        if visible:
            # force：新进程不知道上一个进程有没有把指针藏起来，启动时无条件还原一次
            if _pointer_hidden["value"] or force:
                lib.XFixesShowCursor(_DPY, root)
                _pointer_hidden["value"] = False
                log("cursor: 显示")
            if _pointer_hidden["timer"]:
                try:
                    GLib.source_remove(_pointer_hidden["timer"])
                except Exception:
                    pass
                _pointer_hidden["timer"] = 0
        else:
            if not _pointer_hidden["value"]:
                lib.XFixesHideCursor(_DPY, root)
                _pointer_hidden["value"] = True
                log("cursor: 隐藏（控制已交还 Windows）")
                _pointer_hidden["timer"] = GLib.timeout_add_seconds(
                    POINTER_HIDE_TIMEOUT, _pointer_timeout)
        _x11.XFlush(_DPY)
    except Exception as e:
        log("cursor toggle failed: %r" % (e,))


def mouse_move(payload):
    """M <dx> <dy>：相对移动指针。先累加，等这批 M 行处理完由 flush_move 一次注入。"""
    global _pending_dx, _pending_dy
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
    for st in list(CONNS.values()):
        try:
            st["sock"].sendall(data)
            sent += 1
        except OSError:
            pass
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
        # Windows 说：这台 Ubuntu 的指针该不该显示
        set_pointer_visible(line[4:].strip() != "0")
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
        release_mouse_buttons("disconnect")
        # 客户端走了，没人再控制这台机器，指针必须恢复可见
        set_pointer_visible(True)
        refresh_tray()
    log(msg)
    return False


srv = None


# ---------- 文件接收（独立端口：一行头 + 原始字节） ----------

FILE_MAX_NAME = 180


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
            # 断点续传握手：告诉发送方 .part 里已经有几个字节
            raw = parts[2]
            name, _, tail = raw.rpartition(" ")
            if not tail.isdigit():
                name = raw
            name = safe_name(name)
            dest = unique_path(resolve_file_dir(name), name)
            have = resume_offset(dest, name, size)
            log("resume query: %s have %d/%d" % (name, have, size))
            conn.sendall(("HAVE %d\n" % have).encode("utf-8"))
            return

        if parts[0] == "CLIPIMG":
            # 剪贴板图片：只进内存，不落盘。
            # 第三段是可选文本长度；旧版客户端这里放的是文件名，解析失败按 0 处理。
            try:
                caption_len = int(parts[2])
            except ValueError:
                caption_len = 0
            if caption_len < 0:
                caption_len = 0

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

        # 第四段是可选的续传偏移；旧版客户端只发文件名，这里解析不到数字就当从头传
        raw = parts[2]
        name, _, tail = raw.rpartition(" ")
        offset = 0
        if tail.isdigit():
            offset = int(tail)
        else:
            name = raw
        name = safe_name(name)
        directory = resolve_file_dir(name)
        os.makedirs(directory, exist_ok=True)
        dest = unique_path(directory, name)
        temp_dest = dest + ".part"

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
    sock = socket.create_connection((host, port), 5)
    try:
        sock.sendall(("UPLOAD %d %s\n" % (size, os.path.basename(path))).encode("utf-8"))
        with open(path, "rb") as source:
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


def outbox_worker(path):
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
            log("outbox -> %s: %s" % (peer, os.path.basename(path)))
        else:
            log("outbox failed: %s" % reply)
    except Exception as e:
        log("outbox error: %r" % (e,))


def scan_outbox():
    try:
        directory = outbox_dir()
        if not os.path.isdir(directory):
            os.makedirs(directory, exist_ok=True)
        if os.path.isdir(directory):
            for name in sorted(os.listdir(directory)):
                path = os.path.join(directory, name)
                if os.path.isfile(path):
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
