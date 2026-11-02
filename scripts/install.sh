#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
arch="$(uname -m)"
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
