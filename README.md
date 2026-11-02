# RouteMeter

A native macOS menu bar companion for **Quantumult X**. See **Direct** and **Proxy** traffic separately, with explicit **Download**, **Upload**, and **Total** usage.

[中文说明](docs/README.zh-CN.md) · [Downloads](https://github.com/MatchalatteR/RouteMeter/releases) · [Report a bug](https://github.com/MatchalatteR/RouteMeter/issues)

**Windows preview:** the `windows-mihomo` branch adds a native taskbar meter for Clash Verge / Mihomo, with an independent authenticated TCP/UDP metering relay and no kernel replacement. Proxy transport bytes are counted during forwarding; Direct remains a sampling reference. See [Windows setup, scope, and build instructions](windows/README.zh-CN.md). The macOS/QX implementation below is unchanged.

![RouteMeter interface illustration with synthetic example data](docs/preview.svg)

RouteMeter reads QX's own classified counters. It does not estimate proxy usage from process names or subtract a VPN interface from all network traffic.

- Readable, single-line menu bar: `Direct ↓… ↑… | Proxy ↓… ↑…`.
- A scrollable panel that stays within the current screen's available height.
- Direct / Proxy / Total rates and cumulative Download / Upload / Total columns.
- Persistent cumulative usage, a Reset button, a small recent-history chart, and optional launch at login.
- A local QX bridge with per-install credentials, no analytics, and no cloud service.

**Status:** first public release, **0.1.0**. This is a maintained starting point, not a claim of billing accuracy or validation across every macOS/QX version. QX is a separate paid application; RouteMeter is not affiliated with its developer.

## Requirements

- macOS **14 or newer**.
- Quantumult X for macOS, running with its task switch enabled.
- Apple Silicon or Intel build matching your Mac.
- To build from source: Apple Command Line Tools with Swift 5.9+ and Python 3. Full Xcode is required to run the XCTest suite on macOS.

Live integration was checked with QX 1.8.0 (946) on Apple Silicon. See the [validation notes](docs/VALIDATION.md) for the exact scope. CI tests the core and builds both architectures; an Intel build does not by itself establish validation on Intel hardware.

## Install

Download the matching ZIP from [Releases](https://github.com/MatchalatteR/RouteMeter/releases), extract it, and move **RouteMeter.app** into `~/Applications` or `/Applications`. Then open it.

The initial builds are **ad-hoc signed, not Apple Developer ID signed or notarized**. Downloaded copies may be blocked by Gatekeeper. If your Mac blocks the release, build the app locally from the reviewed source, or follow Apple's normal guidance for trusted software. RouteMeter does not require disabling Gatekeeper or granting administrator access.

To build and install locally:

```sh
git clone https://github.com/MatchalatteR/RouteMeter.git
cd RouteMeter
./scripts/install.sh
```

Quit an existing RouteMeter instance before replacing it. The installer preserves the prior application as a backup and leaves your QX configuration unchanged.

## Setup Quantumult X

1. Open RouteMeter. Click **Copy QX task**.
2. Open QX's **Tasks** panel. Choose **Add → Add as text** (计划任务 → 添加 → 文本方式添加).
3. Paste the copied line and save it.
4. Update/download that task's script once. Keep both the task and QX's main task switch enabled.
5. Leave RouteMeter and the QX tunnel running. The panel should show **QX connected** within a few seconds.

The generated task uses a six-field cron schedule, `*/2 * * * * *`, and a private, random loopback script URL. **Use your own copied line**; another person's URL is not portable. Do not post your generated task URL or local configuration in public issues.

No QX tunnel restart is required for this setup. No routing rules, proxy nodes, TLS interception, or QX HTTP Backend are changed. If QX requests permission to access the local network, it needs to be able to reach the receiver on your own Mac.

If you prefer the command line, run the installed executable with `--print-task`. This prints the same local setup line and creates local configuration on first use.

## Reading the numbers

- **Direct**: QX entries whose name is `direct`.
- **Proxy**: all valid proxy-node TCP/UDP entries combined. Reject/discard entries are excluded.
- **Total**: `Direct + Proxy`, calculated independently for Download and Upload.
- Each cumulative row's **Total**: `Download + Upload`.
- `↓` in the menu bar means **Download**; `↑` means **Upload**. All menu bar values are rates in bytes per second. K/M/G use 1024-based units; the panel spells out KB/s, MB/s, and so on.

The first valid snapshot establishes a baseline. Later snapshots contribute counter differences. New nodes contribute their counters when first observed after the initial baseline. Totals and the baseline persist across app restarts. They can include traffic while RouteMeter was closed if QX's counters continue within the same session. **Reset** creates a fresh baseline on the next valid snapshot.

A drop in any tracked counter rebases the whole snapshot, so a QX restart cannot create a negative or enormous delta. Traffic across unobserved counter resets cannot be recovered. If no valid snapshot arrives for nine seconds, live values become `—` and the panel reports the interruption.

**Scope:** only traffic QX accounts for. This is not an all-interface network meter, ISP/airport billing meter, or per-application monitor. LAN traffic and requests bypassing QX can be absent. Retransmissions and transport overhead can differ from a provider's billing.

## Privacy and local files

Configuration, rendered bridge code, and cumulative state live in `~/Library/Application Support/RouteMeter/`. The directory is mode `0700`; files are `0600`. State contains node names and aggregate counters, not browsing destinations or request bodies.

The app listens only on **127.0.0.1:18764**. The QX bridge only posts to that address. Its six-field allowlist contains a node/category name, TCP/UDP type, byte counters, and current rates. There is no telemetry, account, subscription upload, or background external request. The **Setup guide** button opens this public repository in your browser.

The script download path and ingest credential are generated independently for each installation. HTTP is deliberately limited: capped headers/body, four-second connection timeout, bounded simultaneous connections, authenticated ingestion, and rejection of browser Origin headers or unexpected Host headers. This is not a protection boundary against software already running as your user or as an administrator.

For a port conflict, quit duplicate instances first. Advanced users can quit the app, set a free `port` (1024–65535) in `config.json`, reopen RouteMeter, and replace/update the QX task using a newly copied line. Do not share the rest of that file.

## Stop or uninstall

1. Disable the **RouteMeter** task in QX. Keep your other QX tasks unchanged.
2. Turn off **Launch at login** in RouteMeter, then click **Quit**.
3. Move RouteMeter.app to Trash. Keep its application data if you want to preserve cumulative totals; move that folder to Trash to remove the data too.

Quitting RouteMeter does not stop QX. If its QX task remains enabled, failed local posts end silently. If the app has already been removed, its login item is `~/Library/LaunchAgents/io.github.MatchalatteR.RouteMeter.plist`.

## Development

```sh
swift build --build-system native
swift test --build-system native  # full Xcode, for XCTest
python3 scripts/smoke-test.py "$(swift build --build-system native --show-bin-path)/RouteMeter"
node scripts/test-bridge.cjs
./scripts/package.sh arm64        # or x86_64
```

The integration test creates a temporary private directory and uses a free loopback port. It does not post test counters into your running widget or change QX. There are no third-party Swift package dependencies. The `--headless` executable option exists for integration checks; normal use is through RouteMeter.app.

See [Contributing](CONTRIBUTING.md), [Security](SECURITY.md), and the [architecture notes](docs/ARCHITECTURE.md). Tagged releases are built and published only after the CI checks pass.

## Credits and license

Uses Quantumult X's documented [`get_traffic_statistics` API](https://github.com/crossutility/Quantumult-X/blob/master/sample-get-traffic-statistics.js) and [task scheduling format](https://github.com/crossutility/Quantumult-X/blob/master/sample.conf). Quantumult X itself is not included.

RouteMeter is released under the [MIT License](LICENSE).
