# Contributing

Keep the app small, native, and focused on QX's classified aggregate counters. Preserve the Direct / Proxy / Total and Download / Upload terminology.

Use Swift 5.9+ and macOS 14+. Full Xcode is needed for `swift test`; Command Line Tools alone may not provide XCTest. Run the unit suite, real receiver integration, and bridge contract checks listed in the README. Tests must use a temporary directory and free port; never alter a contributor's live QX configuration or send synthetic samples to their normal widget.

Do not add a subscription URL, node credential, local setup URL, rendered bridge, private config/state, browsing destination, or personal screenshot to a commit or issue. Use synthetic node names in tests and documentation. `scripts/check-public.py` is a small additional check, not a complete secret scanner.

Changes to counter semantics need tests for baseline, missing/new nodes, counter decreases, persistence, and Reset. Changes to the local receiver need limits and negative-request tests. Keep network parsing off the main queue and UI/persistence state updates on the main thread.

To report a bug, include macOS version, CPU architecture, QX version, RouteMeter version, whether the panel reports stale data or an unavailable receiver, and reproduction steps. Redact node names and local setup URLs.
