# RouteMeter Core setup (Windows and macOS)

RouteMeter 0.2 has two components: a native viewer and **RouteMeter Core**, a separately built Mihomo-derived routing core. For per-App Proxy usage, the actual application traffic must pass through RouteMeter Core. A stock QX or stock Mihomo connection-list snapshot does not provide these lifecycle counters.

## Measurement boundary

- Counts are recorded at the actual routing adapter's reads/writes, including fast paths and UDP.
- TCP connections that close between UI refreshes remain in the cumulative counters.
- Direct/Proxy is determined from the actual leaf adapter type, not a policy/group/node name.
- Source ownership is looked up at connection creation using the OS-backed resolver. Missing or ambiguous owners go to **Unattributed**. Unknown adapter types go to **Unknown routing**.
- On macOS, executable paths inside the same outer `.app` bundle group under that App. On Windows, executable identities use a normalized, case-insensitive full path. Separate programs with the same basename are not merged.
- These are core-routed application-payload bytes, not encrypted wire bytes, retransmissions, ISP billing, traffic outside this core, or retrospective statistics for another VPN/proxy.

The viewer never infers Proxy from a TUN interface or destination country. If another VPN or proxy reroutes the core's transport, Direct/Proxy here still describes this core's adapter decision. Use one deliberate routing design for meaningful final-egress accounting.

## 1. Start the core with your routing configuration

Use a **Mihomo-compatible configuration** containing your intended nodes and Direct/Proxy rules. Keep credentials in your private local file. Do not upload it to an issue.

Windows PowerShell (from the extracted release directory):

```powershell
$coreHome = Join-Path $env:LOCALAPPDATA "RouteMeter\Core"
.\RouteMeterCore.exe -d $coreHome -f "C:\path\to\your-private-config.yaml"
```

macOS (adjust the application location):

```sh
"$HOME/Applications/RouteMeter.app/Contents/Helpers/RouteMeterCore" \
  -d "$HOME/Library/Application Support/RouteMeter/Core" \
  -f "$HOME/path/to/your-private-config.yaml"
```

The core always attempts process lookup even if the input configuration disables it. Other routing settings remain defined by the chosen configuration. The statistics viewer does not change OS proxy settings, DNS, or routes.

Configure the application/system proxy to use the core's actual mixed/HTTP/SOCKS inbound, or configure a supported TUN mode if whole-device coverage is required. TUN setup can require elevated privileges and separate OS approval. This is a network configuration step: verify Direct and Proxy behavior and keep a rollback configuration before replacing an existing daily VPN/proxy. No TUN or global-proxy cutover is performed by merely opening the viewer.

## 2. Connect the viewer

Open RouteMeter and choose **Connect Core…**. Select:

- Windows: `%LOCALAPPDATA%\RouteMeter\Core\route-meter\connection.json`
- macOS: `~/Library/Application Support/RouteMeter/Core/route-meter/connection.json`

The core creates this file with a random read-only API credential and a loopback endpoint. The endpoint can change across restarts; the viewer re-reads the same file. Do not copy its token into a public report. The statistics API supports only authenticated reads; it cannot change routes, kill connections, or expose full configuration.

macOS also retains the old **QX legacy totals** source. QX mode does not offer per-App Proxy attribution. Switching viewer sources does not migrate or alter your proxy service.

## Reading and resetting

The main counters show Direct, Proxy, Unknown routing (when nonzero), and Total. **Proxy usage by App** shows only the Proxy bucket for each executable/App, with Download, Upload, and Total. The live-rate option is a display derived from cumulative counters; the underlying cumulative accounting does not depend on polling.

Reset affects that viewer's baseline only. Other viewers and the core's persisted lifetime totals are unchanged. Unattributed Proxy bytes are visible rather than assigned to a guessed App. Apps and totals reconcile within each snapshot.

## Persistence and warnings

`<core-home>/route-meter/usage.json` contains cumulative counters and executable identities, not destination IPs/domains, URLs, headers, or request bodies. Normally the core checkpoints atomically every second and flushes on normal shutdown. A force-stop/power loss can lose data since the last successful checkpoint; slow or failing storage can extend that interval. Storage failures are explicitly reported. Invalid saved state is retained with an unreadable-state filename and a new epoch is marked.

The directory has a single-writer lock. Two core instances cannot overwrite the same meter data. On Unix it is mode 0700 with private files; Windows applies a private ACL for the current user, SYSTEM, and administrators. This is not an isolation boundary against the same user or an administrator.

Failed buffered writes with an unknown partial byte count are flagged as uncertain and excluded from precise successful-byte totals. Overflow and unknown routing are also visible warnings.

## Exit and compatibility

Exiting the viewer leaves the routing core running. Stop a console-launched core with Ctrl+C when you intend to stop that proxy service. Preserve its data folder to retain cumulative usage.

The native Windows viewer targets Windows 10/11 x64 and includes its .NET runtime in release packages. The macOS viewer requires macOS 14+. Standard HTTP/SOCKS tests run on native Windows and macOS CI. These checks do not establish every third-party App, protected system service, TUN configuration, Windows ARM64 machine, or long-duration/whole-device deployment. Unsupported ownership remains Unattributed instead of receiving a fabricated label.
