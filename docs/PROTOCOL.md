# Lifecycle metering protocol, schema 2

The core creates a private `connection.json`: schema 1, an IPv4 loopback `endpoint`, and a random 256-bit hex `token`. Native clients read `GET <endpoint>/v1/usage` with `Authorization: Bearer <token>`. The endpoint is independent of Mihomo's controller API and exposes no configuration/mutation endpoints. The endpoint/token rotate on core start; clients re-read the connection file.

The response includes:

- `schema: 2`, `program: RouteMeterCore`, `measurement: routing-core-payload-lifecycle`.
- `epoch`: persistent counter generation. `instance`: current process generation. `since`, `timestamp`, `checkpoint`: Unix milliseconds.
- `sequence`: measurement/state mutation sequence.
- `totals.direct`, `totals.proxy`, `totals.unknown`: unsigned `download` and `upload` byte counters.
- `apps`: stable identity, display name, executable/bundle path, attribution status, each routing bucket's counters, active count, and lifetime connection count.
- `uncertainUpload`, `overflow`, and `persistenceError`: explicit quality/durability indicators.

For every snapshot, the componentwise sum over Apps equals the global totals, including the Unattributed owner. Unknown routing is a distinct bucket; it never silently becomes Proxy. A Direct adapter with a custom/dialer-proxy transport is kept Unknown when its final transport cannot be proven. Bytes are counted at actual adapter I/O paths, including zero-copy callbacks and UDP wrappers. Connection close only changes the active count; cumulative bytes remain.

macOS App identity groups the outermost `.app` bundle. Windows identity normalizes path separators and case. Missing or ambiguous OS-backed source resolution uses `id: unknown` and name `Unattributed`. The identity describes the OS-owning executable; shared background services may appear under their service process rather than an initiating GUI App. No domain, user-agent, interface, destination-country, node-name, or currently selected group heuristic supplies an App identity or route.

Clients validate the schema marker, freshness, unique identities, and conservation. They accept loopback manifests only and do not follow HTTP redirects or inherit a system HTTP proxy for these requests. They derive rates from cumulative snapshots; a missed refresh can affect the rate display but cannot erase completed-flow usage.

Reset is a viewer-local baseline, keyed by epoch. Core restarts preserve the epoch after a valid persisted restore and use a new instance ID. Invalid state is retained locally and produces a new epoch with an error. Normal checkpoint interval is one second. A forced stop loses any data not yet checkpointed; disk errors or delayed checkpoints can extend this interval. This protocol is not a guarantee of recovery after power loss or a billing/wire-byte meter.
