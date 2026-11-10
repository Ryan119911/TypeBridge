#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""TypeBridge Ubuntu 设置页。"""

import json
import os
import re
import shutil
import subprocess
from pathlib import Path

import gi

gi.require_version("Gtk", "3.0")
gi.require_version("Gdk", "3.0")
from gi.repository import Gdk, GLib, Gtk

SERVICE = "typebridge.service"
CONF_DIR = Path.home() / ".config" / "typebridge"
CONF_FILE = CONF_DIR / "config.json"
DEFAULT_PORT = 4321


def run_command(args, timeout=8):
    try:
        return subprocess.run(
            args,
            capture_output=True,
            text=True,
            timeout=timeout,
        )
    except Exception as exc:
        return subprocess.CompletedProcess(args, 1, "", str(exc))


def user_systemctl(*args):
    return run_command(["systemctl", "--user", *args])


def load_config():
    try:
        with open(CONF_FILE, "r", encoding="utf-8") as handle:
            data = json.load(handle)
            return data if isinstance(data, dict) else {}
    except Exception:
        return {}


def save_config(port):
    data = load_config()
    data["port"] = int(port)
    data["auto_deactivate_fcitx"] = False
    CONF_DIR.mkdir(parents=True, exist_ok=True)
    with open(CONF_FILE, "w", encoding="utf-8") as handle:
        json.dump(data, handle, ensure_ascii=False, indent=2)
        handle.write("\n")


def detect_addresses():
    addresses = []
    seen = set()

    if shutil.which("tailscale"):
        result = run_command(["tailscale", "ip", "-4"])
        for ip in (result.stdout or "").split():
            if ip not in seen:
                addresses.append(("Tailscale", ip))
                seen.add(ip)

    result = run_command(["ip", "-4", "-o", "addr", "show", "scope", "global"])
    for line in (result.stdout or "").splitlines():
        match = re.match(r"^\d+:\s+(\S+)\s+inet\s+([0-9.]+)/", line)
        if not match:
            continue
        iface, ip = match.group(1), match.group(2)
        if iface.startswith(("docker", "br-", "veth", "virbr", "lo")):
            continue
        if ip not in seen:
            addresses.append((iface, ip))
            seen.add(ip)

    return addresses


class TypeBridgeWindow(Gtk.Window):
    def __init__(self):
        super().__init__(title="TypeBridge")
        self.set_default_size(720, 650)
        self.set_size_request(640, 560)
        self.set_icon_name("typebridge")
        self.set_position(Gtk.WindowPosition.CENTER)

        self.config = load_config()
        self.active = False
        self.loaded = False

        header = Gtk.HeaderBar()
        header.set_show_close_button(True)
        header.set_title("TypeBridge")
        header.set_subtitle("Ubuntu 输入桥")
        self.set_titlebar(header)

        scroll = Gtk.ScrolledWindow()
        scroll.set_policy(Gtk.PolicyType.NEVER, Gtk.PolicyType.AUTOMATIC)
        self.add(scroll)

        outer = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=14)
        outer.set_border_width(18)
        scroll.add(outer)

        title = Gtk.Label(xalign=0)
        title.set_markup("<span size='x-large' weight='bold'>TypeBridge Ubuntu</span>")
        outer.pack_start(title, False, False, 0)

        subtitle = Gtk.Label(
            label="接收 Windows 键盘输入，使用本机 Ubuntu 输入法完成中文选字。",
            xalign=0,
        )
        subtitle.set_line_wrap(True)
        outer.pack_start(subtitle, False, False, 0)

        outer.pack_start(self.build_status_card(), False, False, 0)
        outer.pack_start(self.build_address_card(), False, False, 0)
        outer.pack_start(self.build_settings_card(), False, False, 0)

        self.message_label = Gtk.Label(xalign=0)
        self.message_label.set_line_wrap(True)
        outer.pack_start(self.message_label, False, False, 0)

        self.refresh_status()
        self.refresh_addresses()
        GLib.timeout_add_seconds(2, self.refresh_status)

    def build_status_card(self):
        frame = Gtk.Frame(label="服务状态")
        frame.set_shadow_type(Gtk.ShadowType.IN)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        box.set_border_width(14)
        frame.add(box)

        row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=12)
        self.status_icon = Gtk.Image()
        self.status_icon.set_pixel_size(36)
        row.pack_start(self.status_icon, False, False, 0)

        text_box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=3)
        self.status_label = Gtk.Label(xalign=0)
        self.status_detail = Gtk.Label(xalign=0)
        self.status_detail.set_line_wrap(True)
        text_box.pack_start(self.status_label, False, False, 0)
        text_box.pack_start(self.status_detail, False, False, 0)
        row.pack_start(text_box, True, True, 0)
        box.pack_start(row, False, False, 0)

        buttons = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        self.start_button = Gtk.Button(label="启动")
        self.stop_button = Gtk.Button(label="停止")
        self.restart_button = Gtk.Button(label="重新启动")
        self.start_button.connect("clicked", self.on_start)
        self.stop_button.connect("clicked", self.on_stop)
        self.restart_button.connect("clicked", self.on_restart)
        buttons.pack_start(self.start_button, False, False, 0)
        buttons.pack_start(self.stop_button, False, False, 0)
        buttons.pack_start(self.restart_button, False, False, 0)
        box.pack_start(buttons, False, False, 0)

        return frame

    def build_address_card(self):
        frame = Gtk.Frame(label="Windows 端连接地址")
        frame.set_shadow_type(Gtk.ShadowType.IN)
        box = Gtk.Box(orientation=Gtk.Orientation.VERTICAL, spacing=10)
        box.set_border_width(14)
        frame.add(box)

        hint = Gtk.Label(
            label="在 Windows 的 TypeBridge 设置中填写下面的 IP 和端口。推荐使用 Tailscale 地址。",
            xalign=0,
        )
        hint.set_line_wrap(True)
        box.pack_start(hint, False, False, 0)

        row = Gtk.Box(orientation=Gtk.Orientation.HORIZONTAL, spacing=8)
        self.address_combo = Gtk.ComboBoxText()
        self.address_combo.set_hexpand(True)
        row.pack_start(self.address_combo, True, True, 0)

        copy_button = Gtk.Button(label="复制 IP")
        copy_button.connect("clicked", self.on_copy_address)
        row.pack_start(copy_button, False, False, 0)

        refresh_button = Gtk.Button(label="刷新地址")
        refresh_button.connect("clicked", lambda _button: self.refresh_addresses())
        row.pack_start(refresh_button, False, False, 0)
        box.pack_start(row, False, False, 0)

        self.address_hint = Gtk.Label(xalign=0)
        self.address_hint.set_line_wrap(True)
        box.pack_start(self.address_hint, False, False, 0)

        return frame

    def build_settings_card(self):
        frame = Gtk.Frame(label="服务设置")
        frame.set_shadow_type(Gtk.ShadowType.IN)
        grid = Gtk.Grid(column_spacing=12, row_spacing=10)
        grid.set_border_width(14)
        frame.add(grid)

        port_label = Gtk.Label(label="TCP 端口", xalign=0)
        grid.attach(port_label, 0, 0, 1, 1)

        self.port_spin = Gtk.SpinButton.new_with_range(1, 65535, 1)
        self.port_spin.set_value(int(self.config.get("port", DEFAULT_PORT)))
        grid.attach(self.port_spin, 1, 0, 1, 1)

        save_button = Gtk.Button(label="保存并重启服务")
        save_button.connect("clicked", self.on_save)
        grid.attach(save_button, 2, 0, 1, 1)

        firewall_button = Gtk.Button(label="防火墙放行端口")
        firewall_button.connect("clicked", self.on_firewall)
        grid.attach(firewall_button, 0, 1, 1, 1)

        config_label = Gtk.Label(xalign=0)
        config_label.set_line_wrap(True)
        grid.attach(config_label, 1, 1, 2, 1)
        self.config_label = config_label
        self.update_config_label()

        return frame

    def update_config_label(self):
        if self.config.get("auto_deactivate_fcitx", False):
            self.config_label.set_markup(
                "<span foreground='#c0392b'>当前配置会关闭 fcitx，中文可能无法组词。保存设置会自动修正。</span>"
            )
        else:
            self.config_label.set_text("Ubuntu 输入法保持启用，配置正确。")

    def refresh_status(self):
        load_state = user_systemctl(
            "show", "-p", "LoadState", "--value", SERVICE
        ).stdout.strip()
        active_state = user_systemctl("is-active", SERVICE).stdout.strip()
        enabled_state = user_systemctl("is-enabled", SERVICE).stdout.strip()

        self.loaded = load_state == "loaded"
        self.active = active_state == "active"

        if not self.loaded:
            self.status_icon.set_from_icon_name("dialog-warning", Gtk.IconSize.DIALOG)
            self.status_label.set_markup("<b>服务未安装</b>")
            self.status_detail.set_text("请安装 TypeBridge 的 Ubuntu 安装包。")
            for button in (self.start_button, self.stop_button, self.restart_button):
                button.set_sensitive(False)
        elif self.active:
            self.status_icon.set_from_icon_name("emblem-ok-symbolic", Gtk.IconSize.DIALOG)
            self.status_label.set_markup("<b>运行中</b>")
            self.status_detail.set_text(
                "自动启动：%s　协议端口：%s"
                % (enabled_state or "unknown", self.config.get("port", DEFAULT_PORT))
            )
            self.start_button.set_sensitive(False)
            self.stop_button.set_sensitive(True)
            self.restart_button.set_sensitive(True)
        else:
            self.status_icon.set_from_icon_name("process-stop-symbolic", Gtk.IconSize.DIALOG)
            self.status_label.set_markup("<b>已停止</b>")
            self.status_detail.set_text("自动启动：%s" % (enabled_state or "unknown"))
            self.start_button.set_sensitive(True)
            self.stop_button.set_sensitive(False)
            self.restart_button.set_sensitive(True)

        return True

    def refresh_addresses(self):
        addresses = detect_addresses()
        selected = self.address_combo.get_active_id()
        self.address_combo.remove_all()
        for label, ip in addresses:
            self.address_combo.append(ip, "%s: %s" % (label, ip))
        if addresses:
            if selected and any(ip == selected for _, ip in addresses):
                self.address_combo.set_active_id(selected)
            else:
                self.address_combo.set_active(0)
            self.address_hint.set_text(
                "端口：%s（局域网需要允许该 TCP 端口）" % self.config.get("port", DEFAULT_PORT)
            )
        else:
            self.address_hint.set_text("未检测到可用地址，请检查网络接口或 Tailscale。")

    def selected_ip(self):
        return self.address_combo.get_active_id() or ""

    def show_message(self, text, error=False):
        color = "#c0392b" if error else "#1e8449"
        self.message_label.set_markup(
            "<span foreground='%s'>%s</span>" % (color, GLib.markup_escape_text(text))
        )

    def systemctl_action(self, action, success_text):
        result = user_systemctl(action, SERVICE)
        if result.returncode != 0:
            detail = (result.stderr or result.stdout or "操作失败").strip()
            self.show_message(detail.splitlines()[-1], True)
        else:
            self.show_message(success_text)
        self.refresh_status()

    def on_start(self, _button):
        self.systemctl_action("start", "服务已启动")

    def on_stop(self, _button):
        self.systemctl_action("stop", "服务已停止")

    def on_restart(self, _button):
        self.systemctl_action("restart", "服务已重新启动")

    def on_copy_address(self, _button):
        ip = self.selected_ip()
        if not ip:
            self.show_message("没有可复制的地址", True)
            return
        clipboard = Gtk.Clipboard.get(Gdk.SELECTION_CLIPBOARD)
        clipboard.set_text(ip, -1)
        clipboard.store()
        self.show_message("已复制 IP：%s" % ip)

    def on_save(self, _button):
        port = int(self.port_spin.get_value())
        try:
            save_config(port)
            self.config = load_config()
            self.update_config_label()
            self.refresh_addresses()
            self.systemctl_action(
                "restart" if self.active else "start",
                "端口已保存为 %s，服务已重启" % port,
            )
        except Exception as exc:
            self.show_message("无法保存配置：%s" % exc, True)

    def on_firewall(self, _button):
        if not shutil.which("pkexec") or not shutil.which("ufw"):
            self.show_message("未找到 pkexec 或 ufw，无法自动放行防火墙", True)
            return
        port = int(self.port_spin.get_value())
        try:
            subprocess.Popen(["pkexec", "ufw", "allow", "%s/tcp" % port])
            self.show_message("已打开授权窗口，请确认放行 %s/tcp" % port)
        except Exception as exc:
            self.show_message("无法启动授权程序：%s" % exc, True)


def main():
    window = TypeBridgeWindow()
    window.connect("destroy", Gtk.main_quit)
    window.show_all()
    Gtk.main()


if __name__ == "__main__":
    main()
