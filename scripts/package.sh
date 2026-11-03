#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
arch="${1:-$(uname -m)}"
case "$arch" in arm64|x86_64) ;; *) echo "Use arm64 or x86_64" >&2; exit 1 ;; esac
version="$(cat VERSION)"
swift build --build-system native -c release --arch "$arch" -Xswiftc -file-prefix-map -Xswiftc "$PWD=/RouteMeter" -Xlinker -dead_strip
bin="$(swift build --build-system native -c release --arch "$arch" --show-bin-path)"
stage="dist/$arch"
app="$stage/RouteMeter.app"
mkdir -p "$app/Contents/MacOS" "$app/Contents/Resources"
cp "$bin/RouteMeter" "$app/Contents/MacOS/RouteMeter"
strip -S "$app/Contents/MacOS/RouteMeter"
cp -R "$bin/RouteMeter_RouteMeter.bundle" "$app/Contents/Resources/"
core_arch="$arch"
if [ "$arch" = x86_64 ]; then core_arch=amd64; fi
core="dist/core/darwin-$core_arch/RouteMeterCore"
if [ -f "$core" ]; then
  mkdir -p "$app/Contents/Helpers"
  cp "$core" "$app/Contents/Helpers/RouteMeterCore"
  cp engine/licenses/GPL-3.0.txt "$app/Contents/Resources/LICENSE-Core-GPL-3.0.txt"
  codesign --force --sign - "$app/Contents/Helpers/RouteMeterCore"
fi
python3 - "$app/Contents/Info.plist" "$version" <<'PY'
import plistlib,sys
from pathlib import Path
Path(sys.argv[1]).write_bytes(plistlib.dumps({
 'CFBundleIdentifier':'io.github.MatchalatteR.RouteMeter',
 'CFBundleName':'RouteMeter','CFBundleDisplayName':'RouteMeter',
 'CFBundleExecutable':'RouteMeter','CFBundlePackageType':'APPL',
 'CFBundleShortVersionString':sys.argv[2],'CFBundleVersion':sys.argv[2],
 'LSUIElement':True,'NSHighResolutionCapable':True,'LSMinimumSystemVersion':'14.0',
 'NSHumanReadableCopyright':'Copyright 2026 MatchalatteR. MIT License.'
}))
PY
codesign --force --sign - "$app"
codesign --verify --deep --strict "$app"
archive="dist/RouteMeter-$version-$arch.zip"
ditto -c -k --sequesterRsrc --keepParent "$app" "$archive"
(cd dist && shasum -a 256 "RouteMeter-$version-$arch.zip" > "RouteMeter-$version-$arch.zip.sha256")
echo "Packaged $archive"
