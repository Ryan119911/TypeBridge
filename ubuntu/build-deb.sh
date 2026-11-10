#!/bin/bash
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
VERSION="${VERSION:-3.2.0}"
BUILD="$(mktemp -d)"
trap 'rm -rf "$BUILD"' EXIT

PKG="$BUILD/typebridge_${VERSION}_all"
mkdir -p \
    "$PKG/DEBIAN" \
    "$PKG/usr/lib/typebridge" \
    "$PKG/usr/bin" \
    "$PKG/usr/share/applications" \
    "$PKG/usr/lib/systemd/user"

install -m 755 "$ROOT/typebridge-ubuntu.py" "$PKG/usr/lib/typebridge/typebridge-ubuntu.py"
install -m 755 "$ROOT/ubuntu/typebridge-ui.py" "$PKG/usr/lib/typebridge/typebridge-ui.py"
install -m 755 "$ROOT/ubuntu/typebridge-ui" "$PKG/usr/bin/typebridge-ui"
install -m 644 "$ROOT/ubuntu/typebridge.desktop" "$PKG/usr/share/applications/typebridge.desktop"
install -m 644 "$ROOT/ubuntu/typebridge.service" "$PKG/usr/lib/systemd/user/typebridge.service"

cat > "$PKG/DEBIAN/control" <<EOF
Package: typebridge
Version: ${VERSION}
Section: utils
Priority: optional
Architecture: all
Maintainer: Jia <94781150+Ryan119911@users.noreply.github.com>
Homepage: https://github.com/Ryan119911/TypeBridge
Depends: python3, python3-gi, gir1.2-gtk-3.0, xdotool, policykit-1
Description: Windows to Ubuntu keyboard bridge
 TypeBridge receives keyboard input from the Windows tray client and
 injects it into the focused X11 window on Ubuntu. It ships a GTK
 settings window and a systemd user service.
EOF

cat > "$PKG/DEBIAN/postinst" <<'EOF'
#!/bin/sh
set -e
if command -v systemctl >/dev/null 2>&1; then
    systemctl --global enable typebridge.service >/dev/null 2>&1 || true
fi
exit 0
EOF

cat > "$PKG/DEBIAN/prerm" <<'EOF'
#!/bin/sh
set -e
if command -v systemctl >/dev/null 2>&1; then
    systemctl --global disable typebridge.service >/dev/null 2>&1 || true
fi
exit 0
EOF

chmod 755 "$PKG/DEBIAN/postinst" "$PKG/DEBIAN/prerm"
mkdir -p "$ROOT/dist"
dpkg-deb --build "$PKG" "$ROOT/dist/typebridge_${VERSION}_all.deb"
echo "$ROOT/dist/typebridge_${VERSION}_all.deb"
