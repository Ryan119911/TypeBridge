#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${VERSION:-3.7.8}"
BUILD="$(mktemp -d)"
trap 'rm -rf "$BUILD"' EXIT

PKG="$BUILD/typebridge_${VERSION}_all"
mkdir -p \
    "$PKG/DEBIAN" \
    "$PKG/usr/lib/typebridge" \
    "$PKG/usr/bin" \
    "$PKG/usr/share/applications" \
    "$PKG/usr/lib/systemd/user" \
    "$PKG/usr/share/icons/hicolor"

install -m 755 "$ROOT/typebridge-ubuntu.py" "$PKG/usr/lib/typebridge/typebridge-ubuntu.py"
install -m 755 "$ROOT/typebridge-drop.py" "$PKG/usr/lib/typebridge/typebridge-drop.py"
install -m 755 "$ROOT/ubuntu/typebridge-ui.py" "$PKG/usr/lib/typebridge/typebridge-ui.py"
install -m 755 "$ROOT/ubuntu/typebridge-ui" "$PKG/usr/bin/typebridge-ui"
install -m 755 "$ROOT/ubuntu/typebridge-daemon" "$PKG/usr/lib/typebridge/typebridge-daemon"
install -m 644 "$ROOT/ubuntu/typebridge.desktop" "$PKG/usr/share/applications/typebridge.desktop"
install -m 644 "$ROOT/ubuntu/typebridge.service" "$PKG/usr/lib/systemd/user/typebridge.service"

for size in 16 32 48 64 128 256 512; do
    install -Dm644 "$ROOT/assets/typebridge-${size}.png" \
        "$PKG/usr/share/icons/hicolor/${size}x${size}/apps/typebridge.png"
done
install -Dm644 "$ROOT/assets/typebridge.svg" \
    "$PKG/usr/share/icons/hicolor/scalable/apps/typebridge.svg"

cat > "$PKG/DEBIAN/control" <<EOF
Package: typebridge
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: all
Maintainer: Jia <94781150+Ryan119911@users.noreply.github.com>
Homepage: https://github.com/Ryan119911/TypeBridge
Depends: python3, python3-gi, gir1.2-gtk-3.0, xdotool
Recommends: gir1.2-ayatanaappindicator3-0.1, libnotify-bin, ufw, pkexec | policykit-1
Description: Windows to Ubuntu keyboard bridge
 TypeBridge receives keyboard input from the Windows tray client and
 injects it into the focused X11 window on Ubuntu. It ships a GTK
 settings window and a systemd user service.
EOF

cat > "$PKG/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e

# --global enable 只对之后再登录的会话生效，当前会话得单独拉一次
if command -v systemctl >/dev/null 2>&1; then
    systemctl --global enable typebridge.service >/dev/null 2>&1 || true
fi

if command -v gtk-update-icon-cache >/dev/null 2>&1; then
    gtk-update-icon-cache -f -t /usr/share/icons/hicolor >/dev/null 2>&1 || true
fi

# 尽力帮"刚执行 sudo apt install 的那个用户"立刻跑起来；拿不到会话就跳过，
# 绝不能让安装失败
if [ -n "${SUDO_USER:-}" ] && command -v runuser >/dev/null 2>&1; then
    target_uid="$(id -u "$SUDO_USER" 2>/dev/null || true)"
    if [ -n "$target_uid" ] && [ -d "/run/user/$target_uid" ]; then
        timeout 8 runuser -u "$SUDO_USER" -- env \
            XDG_RUNTIME_DIR="/run/user/$target_uid" \
            DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$target_uid/bus" \
            systemctl --user daemon-reload >/dev/null 2>&1 || true
        timeout 8 runuser -u "$SUDO_USER" -- env \
            XDG_RUNTIME_DIR="/run/user/$target_uid" \
            DBUS_SESSION_BUS_ADDRESS="unix:path=/run/user/$target_uid/bus" \
            systemctl --user start typebridge >/dev/null 2>&1 || true
    fi
fi

echo "TypeBridge 已安装：在应用列表搜索「TypeBridge」可打开设置面板。"
echo "若图标还没出现，注销重新登录一次即可（服务已设为开机自启）。"
exit 0
EOF

cat > "$PKG/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if command -v systemctl >/dev/null 2>&1; then
    systemctl --global disable typebridge.service >/dev/null 2>&1 || true
    # disable 只删开机自启链接，运行中的用户服务不会停；升级/卸载必须把旧进程收掉，
    # 否则 postinst 的 start 对已 active 的单元是空操作，旧版本一直跑着。
    for runtime in /run/user/[0-9]*; do
        [ -d "$runtime" ] || continue
        uid="${runtime##*/}"
        user="$(id -un "$uid" 2>/dev/null)" || continue
        runuser -u "$user" -- env \
            XDG_RUNTIME_DIR="$runtime" \
            DBUS_SESSION_BUS_ADDRESS="unix:path=$runtime/bus" \
            systemctl --user stop typebridge.service >/dev/null 2>&1 || true
    done
fi
exit 0
EOF

chmod 755 "$PKG/DEBIAN/postinst" "$PKG/DEBIAN/prerm"
mkdir -p "$ROOT/dist"
dpkg-deb --build "$PKG" "$ROOT/dist/typebridge_${VERSION}_all.deb"
echo "$ROOT/dist/typebridge_${VERSION}_all.deb"
