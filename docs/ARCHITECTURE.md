# Architecture

QX owns routing and classification. RouteMeter owns presentation, local persistence, and its loopback receiver.

1. A QX task runs every two seconds and invokes `get_traffic_statistics`.
2. The script allowlists six aggregate fields per entry and POSTs a versioned envelope to `127.0.0.1`.
3. `Receiver` bounds HTTP input and validates the Host and per-install credential.
4. `TrafficAccumulator` validates snapshot time/order/schema, combines TCP/UDP rates, and accumulates counter differences.
5. `MeterModel` publishes the accepted snapshot on the main thread and checkpoints state at most every ten seconds, plus on Reset or normal quit.
6. SwiftUI renders the panel inside a screen-constrained scroll view. AppKit owns the menu bar item and popover.

`RouteMeterCore` contains the counter model, unit formatting, and HTTP parser. It has no UI or external package dependencies. The executable handles OS integration and resources.

## Persistence contract

State schema 1 includes exact unsigned byte counters, the initial/reset time, the last accepted source timestamp, and previous per-node/type counters. A normal app restart resumes deltas; it does not zero totals. A crash can lose up to the most recent ten-second checkpoint interval. Unreadable state is retained locally with a timestamped filename before establishing a new baseline.

The first snapshot is a baseline. Subsequent newly observed nodes contribute their counters from zero. Missing nodes retain their previous baseline, preventing double counting on return. Any decrease in either direction of an existing entry rebases the entire current snapshot. An unobserved QX reset may still hide traffic; no billing or complete-history guarantee is made.

The display becomes stale after nine seconds without accepted data. Persisted rates are never presented as live until a new snapshot arrives. Reordered or excessively old/future samples are ignored.

## Local transport boundary

One IPv4 loopback listener; no all-interface bind, TLS proxy, privileged helper, extension entitlement, or packet capture. Ingestion needs a random 256-bit header credential. Script retrieval needs a separately generated random URL path. Neither belongs in source control or public issues.

The parser accepts narrowly scoped HTTP/1.1 requests with unique headers and an explicit body length. It rejects browser Origin headers, unexpected Host headers, chunking, pipelining, overlong headers/body, and unbounded connection lifetimes. CORS is not enabled. These measures reduce accidental or browser-origin access, but cannot isolate data from another process running as the same user or an administrator.

The script template is public; each rendered instance is private. A setup URL fetch returns only the generated bridge script. No API serves configuration or saved state. Errors never echo credentials or request bodies.
