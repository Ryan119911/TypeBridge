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

文件通道（默认 4322/4323，独立连接，一行头 + 原始字节）:
  UPLOAD <字节数> <文件名>   收文件并落盘
  CLIPIMG <字节数> <文件名>  收图片直接写剪贴板（图片同步，双向都用它）

注入策略:
  ASCII            -> xdotool type（真实按键）
  非 ASCII(中文等) -> 写入剪贴板(CLIPBOARD+PRIMARY) + 按窗口类别发送粘贴键
  粘贴键按窗口类选择, 可用 ~/.config/typebridge/routes.json 覆盖:
      {"窗口类名小写": "ctrl_v" | "ctrl_shift_v" | "shift_insert" | "type"}
"""
import faulthandler
import json
import os
import re
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
    for st in list(CONNS.values()):
        try:
            st["sock"].sendall(data)
        except OSError:
            pass


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


def set_clipboard_image(data):
    """Windows -> Ubuntu：把 PNG 字节写进本机剪贴板（必须在主循环线程里调用）。"""
    if not data:
        return
    _clipboard_state["image_applied"] = data
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
        cb.set_image(pixbuf)
        cb.store()
        log("clipboard <- windows image (%dx%d, %d bytes)"
            % (pixbuf.get_width(), pixbuf.get_height(), len(data)))
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
        sock.sendall(("CLIPIMG %d clip.png\n" % len(data)).encode("utf-8"))
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


def handle_upload(conn, addr):
    try:
        conn.settimeout(30)
        stream = conn.makefile("rb")
        header = stream.readline(4096).decode("utf-8", "replace").strip()
        parts = header.split(" ", 2)
        if len(parts) != 3 or parts[0] not in ("UPLOAD", "CLIPIMG"):
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

        if parts[0] == "CLIPIMG":
            # 剪贴板图片：只进内存，不落盘
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
            # GTK 剪贴板只能在主循环线程里操作，这里排队过去
            GLib.idle_add(set_clipboard_image, bytes(data))
            conn.sendall(b"OK clipboard\n")
            return

        name = safe_name(parts[2])
        directory = resolve_file_dir(parts[2])
        os.makedirs(directory, exist_ok=True)
        dest = unique_path(directory, name)

        received = 0
        started = time.time()
        temp_dest = dest + ".part"
        with open(temp_dest, "wb") as out:
            while received < size:
                chunk = stream.read(min(1 << 16, size - received))
                if not chunk:
                    break
                out.write(chunk)
                received += len(chunk)

        if received != size:
            log("file: incomplete %s (%d/%d) from %s" % (name, received, size, addr[0]))
            try:
                os.remove(temp_dest)
            except OSError:
                pass
            conn.sendall(b"ERR incomplete\n")
            return

        # 原子替换：要么是完整的旧文件，要么是完整的新文件，不会留下半截或副本
        os.replace(temp_dest, dest)

        elapsed = max(0.001, time.time() - started)
        log("file <- %s (%d bytes, %.1f MB/s) -> %s"
            % (name, size, size / elapsed / 1048576.0, dest))
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
