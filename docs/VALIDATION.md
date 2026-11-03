# Validation scope

## 0.2 lifecycle-meter milestone

0.2.0 package testing on the macOS 27 development machine exposed a signing-dependent raw source-lookup failure that macOS 15 CI did not catch. The release was reclassified as preview, with the limitation added to its notes. 0.2.1 adds a same-user, read-only system-tool fallback; an explicitly signed local core passed exact TCP/UDP payload, real executable ownership, native viewer protocol and restart checks with that fallback forced. Release validation must also run against the downloaded signed App's embedded helper.

The common core and two native clients have passed real isolated tests on **native macOS and Windows CI runners**. These are runtime tests, not only cross-compilation. The first Windows attempt exposed checkout line-ending differences; LF attributes and normalized patch preparation address that portability issue.

The loopback integration verifies:

- 25 completed Proxy TCP flows and 25 completed Direct TCP flows, with exact known payload bytes in both directions; all flows may close before the viewer samples.
- Proxy and Direct UDP datagrams, with exact application-payload counts.
- A proxy group with a Direct-looking name and a Direct adapter with a Proxy-looking name; actual adapter types determine the bucket.
- Two real client executables (Python and curl), with nonempty OS-backed executable ownership.
- Componentwise App/global conservation, including explicit unknown buckets.
- The actual native viewer executable reads and validates the real core's HTTP/JSON protocol.
- Native Windows viewer schema tests reject missing fields, inconsistent totals, stale data, duplicate identities and non-loopback connection files.
- Counters survive closed flows and a core restart after a successful checkpoint.

The pure metering package also tests concurrent accounting, persistent epochs, corrupt-state preservation, unknown source/route, private read-only HTTP authentication, error/overflow visibility, and exclusive writer locking. macOS runs the Go race detector. The Swift suite tests aggregate compatibility, HTTP parsing, core schemas and conservation. See the current commit's Actions result for the precise suite counts.

Development Mac: Apple Silicon, macOS 27.0.1, Swift 6.4; pinned core base Mihomo v1.19.33; Go 1.27.3. CI uses macOS 15 and Windows runners with .NET 8. Exact runner image details are recorded in the Actions logs.

## Limits

- Application-payload bytes inside the controlled core are the measurement boundary. Another proxy, VPN, transport overhead, retransmissions or ISP accounting can differ.
- The OS-owning executable is measured. Shared system/background services can appear as that service, rather than the GUI App that scheduled work.
- The tests use explicit local HTTP/SOCKS inbounds and mock upstreams. They do not establish a whole-device TUN deployment, every protected process or third-party App, all network protocols, a personal Windows machine, or long-duration energy use.
- macOS Intel is cross-built; Windows ARM64 and Intel Mac hardware were not runtime-validated by this milestone.
- Normal checkpoint interval is one second, with normal-shutdown flush. A forced termination or storage failure can lose data since the last successful checkpoint. Invalid restored state is visibly flagged, not described as a complete history.
- Native API/client data paths are runtime-tested. Full interactive Windows window/tray behavior, every display scaling/multi-monitor combination, re-login and whole-machine reboot remain broader acceptance work.
- macOS distributions are ad-hoc signed, not Developer ID notarized; Windows packages do not have Authenticode signing yet.

The initial QX 0.1 prototype was verified against real QX aggregate counters. Its panel was enlarged and constrained after user feedback. QX legacy mode remains aggregate-only; the missing QX App/source fields were verified locally and are not substituted with heuristics.
