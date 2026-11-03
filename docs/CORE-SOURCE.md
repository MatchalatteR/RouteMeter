# Building the RouteMeter Core corresponding source

This directory is the complete, patched Mihomo source used to build the separately distributed RouteMeter Core. It includes the GPL-3.0 license, upstream notices, Go module manifests/checksums, and the RouteMeter lifecycle-meter package and integration changes. Upstream revision information is in ROUTEMETER-UPSTREAM.json.

The RouteMeter metering additions and changes in this core are GPL-3.0-only. The Swift and Windows viewer programs communicate over an authenticated, read-only loopback API and retain their own MIT license. The archive includes a vendor directory containing the dependencies selected by the locked module manifests, with their upstream notices.

Build with Go 1.27.3 (the release toolchain):

```sh
CGO_ENABLED=0 go build -mod=vendor -trimpath -buildvcs=false -tags with_gvisor -ldflags '-s -w' -o RouteMeterCore .
go test ./component/routemeter
```

On Windows PowerShell, set `$env:CGO_ENABLED="0"` before the Go build and use `-o RouteMeterCore.exe`. Set GOOS/GOARCH for a cross-build. The source archive supplies the vendored dependencies; the main repository's source-preparation path can obtain them using go.mod and go.sum before generating this archive.

To rebuild the full package and viewers, use the matching RouteMeter tag from https://github.com/MatchalatteR/RouteMeter. Its engine/prepare.py fetches the immutable upstream commit, applies engine/patches/lifecycle-meter.patch, and installs engine/meter. scripts/build-core.sh and the CI workflow contain the exact release commands, including version metadata.
