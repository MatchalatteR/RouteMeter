#!/bin/bash
set -euo pipefail
cd "$(dirname "$0")/.."
"${PYTHON:-python3}" engine/prepare.py
os="${1:-$(go env GOOS)}"
arch="${2:-$(go env GOARCH)}"
case "$os/$arch" in darwin/arm64|darwin/amd64|windows/amd64|windows/arm64|linux/amd64|linux/arm64) ;; *) echo "Unsupported target" >&2; exit 1 ;; esac
mkdir -p "dist/core/$os-$arch"
destination="$PWD/dist/core/$os-$arch/RouteMeterCore"
if [ "$os" = windows ]; then destination="$destination.exe"; fi
version="$(cat VERSION)"
(
  cd .build/engine/mihomo
  CGO_ENABLED=0 GOOS="$os" GOARCH="$arch" go build -trimpath -buildvcs=false -tags with_gvisor -ldflags "-s -w -X github.com/metacubex/mihomo/constant.Version=RouteMeterCore-$version+mihomo1.19.33" -o "$destination" .
)
cp engine/licenses/GPL-3.0.txt "dist/core/$os-$arch/LICENSE-GPL-3.0.txt"
echo "Built $destination"
