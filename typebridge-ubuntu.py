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
import time

faulthandler.enable()

import gi
gi.require_version("Gtk", "3.0")
from gi.repository import Gtk, Gdk, GLib

CONF_DIR = os.path.expanduser("~/.config/typebridge")
CONF_FILE = os.path.join(CONF_DIR, "config.json")
ROUTES_FILE = os.path.join(CONF_DIR, "routes.json")

DEFAULT_CONF = {
    "bind": "0.0.0.0",
    "port": 4321,
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


def release_mouse_buttons(reason):
    """断开连接时松开仍按下的按钮，否则会卡在拖拽状态。"""
    if not PRESSED_BUTTONS:
        return
    for button in sorted(PRESSED_BUTTONS):
        try:
            xdo("mouseup", str(button))
        except Exception as e:
            log("mouse release failed: %r" % (e,))
    log("released mouse buttons %s (%s)" % (sorted(PRESSED_BUTTONS), reason))
    PRESSED_BUTTONS.clear()


def mouse_move(payload):
    """M <dx> <dy>：相对移动指针。"""
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
    if dx == 0 and dy == 0:
        return
    xdo("mousemove_relative", "--", str(dx), str(dy))


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
    if parts[1] == "down":
        PRESSED_BUTTONS.add(button)
    else:
        PRESSED_BUTTONS.discard(button)
    xdo("mousedown" if parts[1] == "down" else "mouseup", str(button))


def mouse_wheel(payload):
    """W up|down|left|right：滚轮。"""
    button = WHEEL_BUTTONS.get(payload.strip())
    if button is None:
        log("bad W payload: %r" % (payload,))
        return
    xdo("click", str(button))


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
    return True


def process_buffer(st):
    sock, buf = st["sock"], st["buf"]
    while b"\n" in buf:
        raw, buf = buf.split(b"\n", 1)
        line = raw.decode("utf-8", "replace").rstrip("\r")
        try:
            if not handle_line(line, sock):
                st["buf"] = buf
                return False
        except Exception as e:
            log("handle error: %r" % (e,))
    st["buf"] = buf
    return True


def on_accept(fd, cond):
    try:
        conn, addr = srv.accept()
    except (BlockingIOError, OSError):
        return True
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


def main():
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
    log("listening on %s:%d" % (bind, port))
    GLib.MainLoop().run()


if __name__ == "__main__":
    main()
