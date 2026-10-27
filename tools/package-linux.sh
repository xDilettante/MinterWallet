#!/usr/bin/env bash
# Packs the Linux single file of the wallet for download:
#   bash tools/package-linux.sh BINARY VERSION OUTDIR
# OUTDIR gets MinterWallet-VERSION-linux-x64 (the file itself), MinterWallet-VERSION-x86_64.AppImage (appimagetool,
# downloaded once into a temporary folder; runs without FUSE) and minter-wallet_VERSION_amd64.deb (dpkg-deb: the file
# in /opt/minter-wallet, `minter-wallet` in /usr/bin, a menu entry and the icon).
# Needs dpkg-deb and desktop-file-validate (desktop-file-utils): without the latter appimagetool builds nothing.
set -euo pipefail
bin=$1; version=$2; out=$3
for tool in dpkg-deb desktop-file-validate curl; do command -v $tool >/dev/null || { echo "$tool is missing (apt install dpkg desktop-file-utils curl)" >&2; exit 1; }; done
root="$(cd "$(dirname "$0")/.." && pwd)"
mkdir -p "$out"; out="$(cd "$out" && pwd)"
work=$(mktemp -d); trap 'rm -rf "$work"' EXIT
icon="$root/packaging/minter-wallet.png"
desktop() {
  printf '[Desktop Entry]\nType=Application\nName=Minter Wallet\nComment=Кошелёк для сети Minter\nExec=%s\nIcon=minter-wallet\nCategories=Office;Finance;\nTerminal=false\n' "$1"
}

install -m 755 "$bin" "$out/MinterWallet-$version-linux-x64"

app=$work/MinterWallet.AppDir
install -D -m 755 "$bin" "$app/usr/bin/MinterWallet"
install -D -m 644 "$icon" "$app/minter-wallet.png"
install -D -m 644 "$icon" "$app/usr/share/icons/hicolor/256x256/apps/minter-wallet.png"
desktop MinterWallet > "$app/minter-wallet.desktop"
printf '#!/bin/sh\nexec "$(dirname "$(readlink -f "$0")")/usr/bin/MinterWallet" "$@"\n' > "$app/AppRun"
chmod 755 "$app/AppRun"
curl -fsSL -o "$work/appimagetool" https://github.com/AppImage/appimagetool/releases/download/1.9.0/appimagetool-x86_64.AppImage
chmod 755 "$work/appimagetool"
ARCH=x86_64 "$work/appimagetool" --appimage-extract-and-run --no-appstream "$app" "$out/MinterWallet-$version-x86_64.AppImage"
[ -s "$out/MinterWallet-$version-x86_64.AppImage" ] || { echo "appimagetool made no AppImage" >&2; exit 1; }

deb=$work/deb
install -D -m 755 "$bin" "$deb/opt/minter-wallet/MinterWallet"
mkdir -p "$deb/usr/bin" "$deb/usr/share/applications" "$deb/DEBIAN"
ln -s /opt/minter-wallet/MinterWallet "$deb/usr/bin/minter-wallet"
desktop minter-wallet > "$deb/usr/share/applications/minter-wallet.desktop"
install -D -m 644 "$icon" "$deb/usr/share/icons/hicolor/256x256/apps/minter-wallet.png"
cat > "$deb/DEBIAN/control" <<EOF
Package: minter-wallet
Version: $version
Architecture: amd64
Maintainer: Dilettante <106393242+xDilettante@users.noreply.github.com>
Depends: libx11-6, libice6, libsm6, libfontconfig1
Section: utils
Priority: optional
Homepage: https://github.com/xDilettante/MinterWallet
Description: Desktop wallet for the Minter network
 Keys stay on this computer, encrypted with a password. Send and multisend,
 swap, staking, history; one self-contained file with .NET inside.
EOF
dpkg-deb --build --root-owner-group "$deb" "$out/minter-wallet_${version}_amd64.deb"
ls -l "$out"
