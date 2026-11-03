# RouteMeter 0.3.0

- Shorter labels, consistent sentence case and readable text in the native viewers.
- macOS setup controls moved into Settings. Repeated formulas and permanent technical footnotes removed from the panel. Measurement details remain in the documentation; active errors remain visible.
- Proxy apps remains accessible in QX mode, with a short Core connection prompt and numbered rankings when connected.
- macOS in-app updates from stable GitHub Releases: matching architecture, SHA-256 and bundle checks, background download, automatic replacement and restart. A private backup and startup acknowledgement allow rollback if launch fails.
- Viewer data and routing settings are preserved. Running routing cores are not restarted. Windows keeps manual release downloads.

# RouteMeter 0.2.2

The metering API now returns 503 until the routing backend has finished its initial configuration. A connectable listening socket is not used as proof of routing readiness. This removes the startup test/client race seen on both Windows and macOS. A regression test verifies the 503-to-200 readiness transition without weakening any payload accounting assertion. The v0.2.1 candidate did not publish because its final Windows runtime check detected this race.

Fix packaged-core App attribution on macOS 27 when raw PCB lookup is restricted for the signed binary. The core uses the supported read-only system ownership utility as the current user, with exact local endpoint matching and ambiguity rejection. No security settings, signatures, privileges or routing settings are weakened. CI now exercises an explicitly signed core and a forced ownership-fallback path. Actual signed-package testing on the development macOS 27 system is required in addition to macOS 15 CI.

The 0.2.0 release is marked preview because its signed Mac package can return all ownership under Unattributed on macOS 27. Payload accounting remains present; that build should not be used as proof of App attribution on macOS 27.

First Windows + macOS lifecycle-meter release.

- Separately distributed RouteMeter Core: pinned Mihomo v1.19.33 with metering at real adapter I/O.
- Closed short TCP connections and UDP remain accounted for after UI polling gaps.
- Actual leaf adapter types determine Direct/Proxy, including misleading custom names and policy groups.
- Per-App executable ownership, macOS bundle grouping, Windows path normalization, visible Unattributed and Unknown-routing buckets.
- Private read-only loopback API, persistent counters, explicit quality warnings and a single-writer lock.
- Native Windows WPF tray viewer and macOS menu bar viewer, each validated against the real protocol.
- macOS QX aggregate compatibility retained; QX does not provide the new App attribution.
- GPL-3.0 core license and complete patched corresponding source included with the release; original viewers remain MIT.

Traffic must actually pass through RouteMeter Core. This is application-payload accounting, not encrypted-wire/ISP billing or retrospective observation of another proxy. OS-wide routing/TUN setup is a separate deployment action. Signing/notarization and broader hardware/TUN/long-duration validation remain open limitations. Read CORE-SETUP and VALIDATION before deployment.
