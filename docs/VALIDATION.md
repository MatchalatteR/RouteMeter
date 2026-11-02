# Validation scope

Initial development environment: Apple Silicon, macOS 27.0.1, QX 1.8.0 (946), Apple Swift 6.4.

- The prototype received real QX Direct/Proxy TCP/UDP counters at approximately two-second intervals.
- The native menu bar/panel was checked with real data. The panel was enlarged, constrained to the current screen's usable height, and checked at both ends of its internal scroll area.
- The public receiver is exercised using its actual executable on a free loopback port. The test covers generated script retrieval, credential checks, invalid JSON, browser/Host rejection, classified persisted totals, and file permissions.
- The QX bridge contract test verifies the loopback target, field allowlist, authorization header, and silent completion on API/network errors.
- XCTest covers baseline behavior, classification, TCP/UDP combination, node appearance/disappearance, restart rebasing, persistent state, reset, stale/out-of-order snapshots, duplicate records, integer limits, and incremental HTTP input.

Full Xcode is required for XCTest on macOS; Command Line Tools alone may build the app but do not supply XCTest. The GitHub Actions workflow runs the XCTest suite under Xcode before building release packages.

Apple Silicon and Intel packages are built separately. A successful cross-build is not an Intel hardware or macOS 14 runtime test. Re-login, whole-machine reboot, sleep/wake, multi-monitor edge cases, and long-duration energy use are not yet established by this initial validation.

Check the repository's current Actions run for the exact commit's CI result. This file describes test coverage and its limits; it is not a permanent assertion that every future build passes.
