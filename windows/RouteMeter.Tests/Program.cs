using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RouteMeter.Core;
using RouteMeter.Windows;

int assertions = 0;
void Check(bool condition, string name) { assertions++; if (!condition) throw new Exception("FAIL: " + name); }
void Reject(string text) { try { Snapshot.Parse(text); throw new Exception("Accepted invalid JSON"); } catch (JsonException) { assertions++; } }
Connection C(string id, ulong down, ulong up, params string[] chains) => new() { Id = id, Download = down, Upload = up, Chains = chains };
Snapshot S(ulong down, ulong up, params Connection[] connections) => new() { DownloadTotal = down, UploadTotal = up, Connections = connections };
var types = new Dictionary<string, string> { ["DIRECT"] = "Direct", ["node"] = "Shadowsocks", ["Policy"] = "Selector", ["custom direct"] = "Direct", ["REJECT"] = "Reject" };
var state = new MeterState(); var meter = new TrafficAccumulator(state);
Check(meter.Ingest(S(1000, 200, C("a", 100, 20, "DIRECT")), types, 0, "test").Rebased, "initial baseline");
var sample = meter.Ingest(S(1500, 300, C("a", 200, 40, "DIRECT"), C("b", 250, 30, "node", "Policy")), types, 1, "test");
Check(sample.Direct == new Bytes(100, 20) && sample.Proxy == new Bytes(250, 30) && sample.Unknown == new Bytes(150, 50), "direct, proxy, short-connection residual");
Check(state.Total == new Bytes(500, 100), "total retains all global bytes");
Check(sample.Total.PerSecond(sample.Seconds) == new Bytes(500, 100), "rate time interval");
sample = meter.Ingest(S(1550, 310, C("a", 1200, 140, "DIRECT"), C("b", 1250, 130, "node")), types, 2, "test");
Check(sample.Total == new Bytes(50, 10) && sample.Unknown == new Bytes(), "asynchronous counters stay inside global budget");
var before = state.Total;
Check(meter.Ingest(S(1, 1, C("new", 0, 0, "DIRECT")), types, 3, "test").Rebased && state.Total == before, "core restart preserves cumulative volume");
Check(meter.Ingest(S(100, 100), types, 4, "other").Rebased && state.Total == before, "source change rebases");
meter.Disconnected();
sample = meter.Ingest(S(120, 130), types, 5, "other");
Check(sample.Seconds == 0 && sample.Unknown == new Bytes(20, 30), "reconnect retains volume without rate spike");
var saved = JsonSerializer.Serialize(state);
var restored = new TrafficAccumulator(JsonSerializer.Deserialize<MeterState>(saved)!);
sample = restored.Ingest(S(130, 140), types, 100, "other");
Check(sample.Total == new Bytes(10, 10) && sample.Seconds == 0, "persisted baseline survives restart");
restored.Reset();
Check(restored.State.Total == new Bytes() && restored.Ingest(S(999, 999), types, 101, "other").Rebased, "reset establishes next baseline");
Check(TrafficAccumulator.Classify(["custom direct", "Policy"], types) == RouteKind.Direct, "custom direct adapter");
Check(TrafficAccumulator.Classify(["node", "DIRECT"], types) == RouteKind.Proxy, "leaf wins over policy group name");
Check(TrafficAccumulator.Classify(["Policy"], types) == RouteKind.Unknown && TrafficAccumulator.Classify(["mystery"], types) == RouteKind.Unknown && TrafficAccumulator.Classify(["REJECT"], types) == RouteKind.Unknown, "unproven routes stay unknown");
Check(Bytes.Format(1024) == "1.0 KB" && Bytes.Format(1024, true) == "1.0 KB/s" && new Bytes(ulong.MaxValue, 1).Total == ulong.MaxValue, "format and overflow");
Reject("{}"); Reject("{\"downloadTotal\":-1,\"uploadTotal\":0,\"connections\":[]}");
Reject("{\"downloadTotal\":0,\"uploadTotal\":0,\"connections\":[{\"id\":\"a\"},{\"id\":\"a\"}]}");
Reject("{\"downloadTotal\":0,\"uploadTotal\":0,\"connections\":null}");
foreach (var bad in new[] { "https://example.com", "http://192.168.0.1:9090", "http://user:password@127.0.0.1:9090", "http://localhost:9090/ui", "http://localhost:9090/?token=x", @"pipe:\\remote\pipe\test" })
{
    try { ApiSource.Manual(bad, ""); throw new Exception("Accepted unsafe endpoint"); } catch (ArgumentException) { assertions++; }
}
var secret = "test-secret-" + Guid.NewGuid();
var encrypted = SecretProtection.Protect(secret);
Check(encrypted != secret && SecretProtection.Unprotect(encrypted) == secret, "DPAPI roundtrip");

// Real HTTP-over-named-pipe integration without touching a running proxy.
var pipeName = "routemeter-test-" + Guid.NewGuid().ToString("N");
using (var server = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous))
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
    var serving = Task.Run(async () =>
    {
        await server.WaitForConnectionAsync(timeout.Token);
        var request = new StringBuilder(); var b = new byte[1];
        while (!request.ToString().EndsWith("\r\n\r\n", StringComparison.Ordinal)) { if (await server.ReadAsync(b, timeout.Token) == 0) throw new IOException(); request.Append((char)b[0]); }
        Check(request.ToString().StartsWith("GET /connections HTTP/1.1"), "read-only connections request");
        var body = "{\"downloadTotal\":123,\"uploadTotal\":45,\"connections\":[]}";
        var bytes = Encoding.UTF8.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\nConnection: close\r\n\r\n" + body);
        await server.WriteAsync(bytes, timeout.Token);
    });
    using var client = new MihomoClient(ApiSource.Manual("pipe:" + pipeName, ""));
    var response = await client.Connections(timeout.Token);
    Check(response.DownloadTotal == 123 && response.UploadTotal == 45, "named pipe transport");
    await serving;
}

// Redirects may never forward credentials outside loopback.
using (var listener = new TcpListener(IPAddress.Loopback, 0))
{
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    var serving = Task.Run(async () =>
    {
        using var connection = await listener.AcceptTcpClientAsync(); using var stream = connection.GetStream();
        var buffer = new byte[4096]; await stream.ReadAsync(buffer);
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 302 Found\r\nLocation: http://example.com/\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
    });
    using var client = new MihomoClient(ApiSource.Manual("http://127.0.0.1:" + port, "fake-test-key"));
    try { await client.Connections(CancellationToken.None); throw new Exception("Followed redirect"); } catch (HttpRequestException) { assertions++; }
    await serving;
}
var folder = Path.Combine(Path.GetTempPath(), "RouteMeter-test-" + Guid.NewGuid().ToString("N"));
try
{
    Storage.Folder = folder; Storage.Save("state.json", state);
    Check(Storage.Load<MeterState>("state.json").Total == state.Total, "atomic state persistence");
    File.WriteAllText(Path.Combine(folder, "state.json"), "corrupt");
    Check(Storage.Load<MeterState>("state.json").Total == new Bytes() && Directory.GetFiles(folder, "*.unreadable-*").Length == 1, "corrupt state retained");
}
finally { if (Directory.Exists(folder)) Directory.Delete(folder, true); }
Console.WriteLine($"PASS {assertions} checks: counters, classification, resets, persistence, DPAPI, pipe HTTP, local-only API, redirects.");
var coverage = MihomoClient.ReadProxyMap("""{"proxies":{"DIRECT":{"type":"Direct"},"group":{"type":"Selector","all":["node"]},"RouteMeter Local Meter":{"type":"Socks5"},"node":{"type":"Shadowsocks","dialer-proxy":"RouteMeter Local Meter"}}}""");
Check(coverage.Coverage.Complete, "API verifies proxy coverage, excluding direct and policy groups");
Check(!MihomoClient.ReadProxyMap("""{"proxies":{"RouteMeter Local Meter":{"type":"Socks5"},"node":{"type":"Vmess","dialer-proxy":""}}}""").Coverage.Complete, "subscription/script changes cannot silently claim complete proxy coverage");
Console.WriteLine("PASS 2 proxy coverage checks.");
int mihomoIndex = Array.IndexOf(args, "--mihomo");
int relayChecks = await RelayTests.Run(mihomoIndex >= 0 && mihomoIndex + 1 < args.Length ? args[mihomoIndex + 1] : null);
Console.WriteLine($"PASS {relayChecks} relay checks: authenticated SOCKS5, exact TCP/UDP byte counts, short connections, half-close, IPv6, restart, direct bypass.");
int hostIndex = Array.IndexOf(args, "--relay-exe");
if (hostIndex >= 0 && hostIndex + 1 < args.Length)
    Console.WriteLine($"PASS {await HostTests.Run(args[hostIndex + 1])} host checks: private IPC, background lifetime, parallel snapshots, durable restart and reset.");
