#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
TypeBridge 拖放区（Ubuntu 端）

把文件从文件管理器拖进这个小窗口，就直接发到 Windows 的
「下载\\typebridge」目录。终端里也可以直接：

    python3 typebridge-drop.py --send ~/图片/a.png
"""
import argparse
import json
import os
import socket
import sys

import gi

gi.require_version('Gtk', '3.0')
from gi.repository import Gtk, Gdk, GLib

CONF_DIR = os.path.expanduser("~/.config/typebridge")
CONF_FILE = os.path.join(CONF_DIR, "config.json")
DEFAULT_CONF = {"host": "127.0.0.1", "port": 4321, "file_recv_port": 4323}


def load_conf():
    conf = dict(DEFAULT_CONF)
    try:
        with open(CONF_FILE, "r", encoding="utf-8") as f:
            data = json.load(f)
            if isinstance(data, dict):
                conf.update(data)
    except Exception:
        pass
    return conf


def peer_ip(conf):
    """向本机守护进程询问最近连上来的 Windows 地址。"""
    try:
        sock = socket.create_connection(("127.0.0.1", int(conf.get("port", 4321))), 3)
        try:
            sock.sendall(b"PEER\n")
            data = sock.recv(128).decode("utf-8", "replace").strip()
        finally:
            sock.close()
        if data.startswith("PEER "):
            address = data[5:].strip()
            if address and address != "0.0.0.0":
                return address
    except Exception:
        pass
    return None


def upload(path, host, port):
    size = os.path.getsize(path)
    name = os.path.basename(path)
    sock = socket.create_connection((host, port), 5)
    try:
        sock.sendall(("UPLOAD %d %s\n" % (size, name)).encode("utf-8"))
        with open(path, "rb") as source:
            while True:
                chunk = source.read(1 << 16)
                if not chunk:
                    break
                sock.sendall(chunk)
        reply = sock.makefile("rb").readline(4096).decode("utf-8", "replace").strip()
        return reply
    finally:
        try:
            sock.close()
        except OSError:
            pass


class DropWindow(Gtk.Window):
    def __init__(self, conf):
        super().__init__(title="TypeBridge 拖放区")
        self.conf = conf
        self.set_default_size(260, 120)
        self.set_keep_above(True)
        self.set_border_width(12)

        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=6)
        self.label = Gtk.Label(label="把文件拖到这里\n→ Windows")
        self.label.set_justify(Gtk.Justification.CENTER)
        self.label.set_line_wrap(True)
        box.pack_start(self.label, True, True, 0)
        self.add(box)

        targets = [Gtk.TargetEntry.new("text/uri-list", 0, 0)]
        self.drag_dest_set(Gtk.DestDefaults.ALL, targets, Gdk.DragAction.COPY)
        self.connect("drag-data-received", self.on_drop)

        self.show_all()

    def on_drop(self, widget, context, x, y, data, info, time):
        paths = []
        for uri in (data.get_uris() or []):
            try:
                path = GLib.filename_from_uri(uri)[0]
                if path and os.path.exists(path):
                    paths.append(path)
            except Exception:
                pass
        Gtk.drag_finish(context, bool(paths), False, time)
        if paths:
            self.send(paths)

    def send(self, paths):
        self.label.set_text("正在发送 %d 个文件…" % len(paths))
        thread = __import__("threading").Thread(target=self.worker, args=(paths,), daemon=True)
        thread.start()

    def worker(self, paths):
        host = peering = peer_ip(self.conf) or self.conf.get("host")
        port = int(self.conf.get("file_recv_port", 4323))
        results = []
        for path in paths:
            try:
                reply = upload(path, host, port)
                results.append("%s: %s" % (os.path.basename(path), "OK" if reply.startswith("OK") else reply))
            except Exception as e:
                results.append("%s: %s" % (os.path.basename(path), e))
        GLib.idle_add(self.finish, results, host)

    def finish(self, results, host):
        text = "已发到 %s\n%s" % (host, "\n".join(results[:4]))
        self.label.set_text(text)
        return False


def main():
    parser = argparse.ArgumentParser(description="TypeBridge 文件拖放区")
    parser.add_argument("--send", nargs="+", help="直接发送这些文件（不开窗口）")
    args = parser.parse_args()

    conf = load_conf()
    if args.send:
        host = peer_ip(conf) or conf.get("host")
        port = int(conf.get("file_recv_port", 4323))
        print("target %s:%d" % (host, port))
        for path in args.send:
            print("%s -> %s" % (path, upload(path, host, port)))
        return 0

    win = DropWindow(conf)
    win.connect("destroy", Gtk.main_quit)
    Gtk.main()
    return 0


if __name__ == "__main__":
    sys.exit(main())
