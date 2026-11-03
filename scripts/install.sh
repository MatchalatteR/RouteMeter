#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
arch="$(uname -m)"
if ! command -v go >/dev/null; then
  echo "Go 1.27.3 is needed to build the bundled Core. Use a prebuilt release or install the build toolchain." >&2
  exit 1
fi
core_arch="$arch"
if [ "$arch" = x86_64 ]; then core_arch=amd64; fi
./scripts/build-core.sh darwin "$core_arch"
./scripts/package.sh "$arch"
destination="$HOME/Applications/RouteMeter.app"
if pgrep -x RouteMeter >/dev/null; then
  echo "Quit RouteMeter from its menu before replacing the application." >&2
  exit 1
fi
mkdir -p "$HOME/Applications"
if [ -d "$destination" ]; then
  backup="$HOME/Applications/RouteMeter-backup-$(date +%Y%m%d-%H%M%S).app"
  mv "$destination" "$backup"
  echo "Previous application backed up in ~/Applications."
fi
ditto "dist/$arch/RouteMeter.app" "$destination"
open "$destination"
echo "Installed. Click Copy QX task in the panel to complete setup."
