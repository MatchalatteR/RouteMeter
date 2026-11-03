# Contributing

Keep the viewers native and the accounting in the routing core. Preserve the Direct / Proxy / Total and Download / Upload terminology. The QX adapter is legacy aggregate-only compatibility.

UI copy uses short labels and direct instructions. Use sentence case, keep labels at 13 pt or larger, and show each fact once. Keep status, usage and actions in the main panel. Put setup controls in Settings and detailed measurement boundaries in the documentation. Show errors only when they apply. Avoid promotional phrases, implementation commentary and repeated accounting formulas.

The macOS updater installs stable releases from this repository only. Keep exact version/architecture selection, SHA-256 checks, bundle validation, an independent install helper, startup acknowledgement and rollback. Run `python3 scripts/test-updater.py dist/arm64/RouteMeter.app` against a signed package. Tests must use temporary app copies and must not modify an installed viewer or a running routing core.

Use Swift 5.9+ and macOS 14+. Full Xcode is needed for `swift test`; Command Line Tools alone may not provide XCTest. Run the unit suite, real receiver integration, and bridge contract checks listed in the README. Tests must use a temporary directory and free port; never alter a contributor's live QX configuration or send synthetic samples to their normal widget.

Do not add a subscription URL, node credential, local setup URL, rendered bridge, private config/state, browsing destination, or personal screenshot to a commit or issue. Use synthetic node names in tests and documentation. `scripts/check-public.py` is a small additional check, not a complete secret scanner.

Changes to accounting need core tests for short completed TCP flows, UDP, parallel fast paths, exact payloads, unknown ownership/routing, uncertain writes, single-writer persistence and restart. Run the real loopback integration on both Windows and macOS. Changes to the local APIs need negative/authentication tests. Keep network parsing off the UI thread. Do not replace lifecycle counters with polling estimates.

Core overlays and upstream patches are GPL-3.0-only; viewers are MIT. Include complete patched corresponding source with core binary releases. Keep the upstream revision immutable and every integration change reviewable in engine/patches.

To report a bug, include macOS version, CPU architecture, QX version, RouteMeter version, whether the panel reports stale data or an unavailable receiver, and reproduction steps. Redact node names and local setup URLs.
