# Architecture

## Controlled data plane

Application → RouteMeter Core inbound → OS-backed source lookup → selected leaf adapter → remote service.

RouteMeter Core is a separately built modified Mihomo, pinned by engine/upstream.json. The integration patch preserves the original transport wrapper layout and adds explicit actual-leaf adapter type propagation. Policy groups append display chains without changing this captured type. The final supported adapter type determines Direct/Proxy; unknown types remain Unknown.

For a counted root connection, the tracker owns one metering handle. Successful reads/writes, buffer paths, zero-copy count callbacks and UDP I/O add payload byte counts to the owning App and route bucket. Nested transport trackers do not double count root application traffic. An errored buffered write with an unavailable partial byte count goes to an explicit uncertainty indicator. Closing a connection only reduces its active count; cumulative counters remain.

Source identity is the OS-owning executable captured when the connection is established. macOS bundles group under the outermost .app, Windows normalizes full-path case/separators. The native process lookup rejects ambiguous multiple-PID source matches. Unresolved sources stay Unattributed. A shared background service is shown as that owning process rather than a guessed initiating GUI App.

## Meter service and persistence

The metering module has one synchronized counter ledger. A snapshot clones all App rows consistently and derives the global route totals from them. Each componentwise App sum equals the corresponding global total.

The core owns an OS-backed exclusive writer lock in a private meter directory. Private atomic checkpoints preserve counters and epoch; a new instance ID marks each process start. Read failures and invalid state are visible, with invalid files preserved locally. Normal checkpoint cadence is one second; any data since the last successful checkpoint can be lost on forced stop or storage failure.

An authenticated, read-only service binds to a random IPv4 loopback port and writes a private connection manifest. It serves only usage and is separate from the routing controller. It exposes no destinations, URLs, payloads, route mutation or connection termination. Unix permissions and Windows ACLs protect the manifest/state; the same OS user or administrator remains outside this isolation boundary.

## Native views

macOS AppKit/SwiftUI and Windows WPF/NotifyIcon consume protocol schema 2. Clients disable inherited HTTP proxies and redirects for this private API, validate loopback connection manifests, freshness, identifiers and App/global conservation, then derive live rates from cumulative snapshots.

Per-App Proxy lists read only the Proxy bucket. View reset subtracts an epoch-keyed local baseline, leaving core totals and other viewers intact. Stale live data becomes a dash. Unknown ownership/routing and quality warnings remain visible. Panel/window height is limited and content scrolls internally.

## Legacy QX compatibility

The old QX script can still push node/Direct aggregates to the macOS viewer. That is a distinct aggregate-only source. The new Core mode does not read QX logs and cannot retrospectively classify traffic routed by another proxy. Actual applications must use the controlled Core inbound for the new attribution feature.

## Distribution and verification

The original native viewers are MIT; the modified routing core and metering integration are GPL-3.0. Release archives include binary notices and complete patched corresponding source with vendored dependencies. Clean native Windows/macOS CI builds the core and viewer and verifies known TCP/UDP payload counts, closed short flows, two real source executables, conservation, native JSON/HTTP compatibility and restart persistence using only temporary loopback fixtures.
