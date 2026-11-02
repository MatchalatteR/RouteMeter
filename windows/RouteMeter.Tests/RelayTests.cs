using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using RouteMeter.Core;
using RouteMeter.Windows;

internal static class RelayTests
{
    private static int checks;
    private static void Check(bool ok, string label) { checks++; if (!ok) throw new Exception("FAIL relay: " + label); }
    public static async Task HostRoundtrip(int port, string user, string password, CancellationToken token)
    {
        await using var echo = new EchoServers();
        await TcpRoundtrip(port, echo.TcpPort, user, password, 45678, token);
    }
    public static async Task<int> Run(string? mihomo)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        await using var echo = new EchoServers();
        RelayLedger checkpoint;
        await using (var relay = new SocksRelay(0, "meter", "test-only-password"))
        {
            relay.Start();
            using (var bad = new TcpClient())
            {
                await bad.ConnectAsync(IPAddress.Loopback, relay.Port, token);
                await bad.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, token);
                var reply = new byte[2]; await bad.GetStream().ReadExactlyAsync(reply, token);
                Check(reply.SequenceEqual(new byte[] { 5, 255 }), "unauthenticated clients refused");
            }
            using (var bad = new TcpClient())
            {
                await bad.ConnectAsync(IPAddress.Loopback, relay.Port, token);
                await Authenticate(bad.GetStream(), "meter", "wrong", token, false);
                Check(relay.Snapshot().Proxy == new Bytes(), "bad authentication never counts traffic");
            }
            int[] sizes = Enumerable.Range(1, 80).Select(i => i * 37).Append(1200003).ToArray();
            await Parallel.ForEachAsync(sizes, new ParallelOptions { MaxDegreeOfParallelism = 8, CancellationToken = token }, async (size, ct) =>
            {
                await TcpRoundtrip(relay.Port, echo.TcpPort, "meter", "test-only-password", size, ct);
            });
            ulong tcpBytes = (ulong)sizes.Sum();
            await Until(() => relay.Snapshot().TcpActive == 0, token);
            Check(relay.Snapshot().Proxy == new Bytes(tcpBytes, tcpBytes), "81 closed/short/large TCP connections counted byte-for-byte");
            await using (var association = await UdpAssociation.Open(relay.Port, "meter", "test-only-password", token))
            {
                for (int i = 1; i <= 30; i++) await association.Roundtrip(echo.UdpPort, new byte[i * 31], token, domain: i % 2 == 0);
                ulong udpBytes = (ulong)Enumerable.Range(1, 30).Sum(i => i * 31);
                Check(relay.Snapshot().Proxy == new Bytes(tcpBytes + udpBytes, tcpBytes + udpBytes), "UDP payload counts exclude local SOCKS headers, including domain address packets");
                var before = relay.Snapshot().Proxy;
                await association.Fragment(echo.UdpPort, token);
                using var stranger = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                await stranger.SendAsync(Datagram(IPAddress.Loopback, echo.UdpPort, new byte[100]), association.Server, token);
                await Task.Delay(80, token);
                Check(relay.Snapshot().Proxy == before, "fragmented and foreign-endpoint UDP packets rejected");
            }
            await Until(() => relay.Snapshot().UdpActive == 0, token);
            Check(relay.Snapshot().UdpActive == 0, "UDP lifetime follows TCP association");
            if (echo.Udp6Port != 0)
            {
                var before = relay.Snapshot().Proxy;
                await using var association = await UdpAssociation.Open(relay.Port, "meter", "test-only-password", token);
                await association.Roundtrip(echo.Udp6Port, new byte[777], token, ipv6: true);
                Check(relay.Snapshot().Proxy == before + new Bytes(777, 777), "IPv6 UDP transport");
            }
            checkpoint = JsonSerializer.Deserialize<RelayLedger>(JsonSerializer.Serialize(relay.Checkpoint()))!;
            Check(checkpoint.Proxy == relay.Snapshot().Proxy, "ledger serialization retains every counted byte");
        }
        await using (var resumed = new SocksRelay(0, "meter", "test-only-password", checkpoint))
        {
            resumed.Start();
            await TcpRoundtrip(resumed.Port, echo.TcpPort, "meter", "test-only-password", 333, token);
            await Until(() => resumed.Snapshot().TcpActive == 0, token);
            Check(resumed.Snapshot().Proxy == checkpoint.Proxy + new Bytes(333, 333), "relay restart resumes cumulative ledger");
            resumed.Reset(); Check(resumed.Snapshot().Proxy == new Bytes(), "reset ledger");
        }
        if (mihomo is not null) await StockMihomo(mihomo, echo, token);
        return checks;
    }
    private static async Task StockMihomo(string path, EchoServers echo, CancellationToken token)
    {
        await using var meter = new SocksRelay(0, "meter", "test-only-password"); meter.Start();
        await using var upstream = new SocksRelay(0, "upstream", "upstream-test-password"); upstream.Start();
        using var directEcho = new TcpListener(IPAddress.Loopback, 0); directEcho.Start();
        int directPort = ((IPEndPoint)directEcho.LocalEndpoint).Port;
        int mixed = FreePort();
        int apiPort = FreePort();
        var directory = Path.Combine(Path.GetTempPath(), "RouteMeter-mihomo-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var config = $"""
            mixed-port: {mixed}
            bind-address: 127.0.0.1
            allow-lan: false
            mode: rule
            log-level: silent
            ipv6: true
            external-controller: 127.0.0.1:{apiPort}
            secret: integration-test-only
            proxies:
              - name: RouteMeter Local Meter
                type: socks5
                server: 127.0.0.1
                port: {meter.Port}
                username: meter
                password: test-only-password
                udp: true
              - name: upstream
                type: socks5
                server: 127.0.0.1
                port: {upstream.Port}
                username: upstream
                password: upstream-test-password
                udp: true
                dialer-proxy: RouteMeter Local Meter
            rules:
              - DST-PORT,{directPort},DIRECT
              - MATCH,upstream
            """;
        var configFile = Path.Combine(directory, "config.yaml"); File.WriteAllText(configFile, config);
        var info = new ProcessStartInfo(Path.GetFullPath(path)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add("-d"); info.ArgumentList.Add(directory); info.ArgumentList.Add("-f"); info.ArgumentList.Add(configFile);
        using var process = Process.Start(info) ?? throw new IOException("Cannot start isolated mihomo");
        var output = process.StandardOutput.ReadToEndAsync(token); var error = process.StandardError.ReadToEndAsync(token);
        try
        {
            await Until(() =>
            {
                if (process.HasExited) throw new Exception("Isolated Mihomo exited: " + output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult());
                try { using var probe = new TcpClient(); return probe.ConnectAsync(IPAddress.Loopback, mixed).Wait(100); } catch (AggregateException) { return false; }
            }, token);
            using (var api = new MihomoClient(ApiSource.Manual("http://127.0.0.1:" + apiPort, "integration-test-only")))
            {
                await api.ProxyTypes(token);
                Check(api.Coverage.Complete, "stock Mihomo API exposes and verifies the complete metering chain");
            }
            for (int i = 1; i <= 15; i++) await TcpRoundtrip(mixed, echo.TcpPort, null, null, i * 99, token);
            await Until(() => meter.Snapshot().TcpActive == 0, token);
            var afterTcp = meter.Snapshot();
            Check(afterTcp.Proxy.Down >= 11880 && afterTcp.Proxy.Up >= 11880 && afterTcp.Accepted >= 15, "unmodified Mihomo routes short TCP proxy connections through meter");
            await using (var association = await UdpAssociation.Open(mixed, null, null, token))
            {
                for (int i = 0; i < 10; i++) await association.Roundtrip(echo.UdpPort, new byte[222 + i], token);
                var afterUdp = meter.Snapshot();
                Check(afterUdp.UdpActive > 0 && afterUdp.Proxy.Down >= afterTcp.Proxy.Down + 2265 && afterUdp.Proxy.Up >= afterTcp.Proxy.Up + 2265, "unmodified Mihomo UDP dialer-proxy forwards and counts every test datagram");
            }
            var beforeDirect = meter.Snapshot().Proxy;
            var directServe = Task.Run(async () =>
            {
                using var connection = await directEcho.AcceptTcpClientAsync(token);
                var bytes = new byte[321]; await connection.GetStream().ReadExactlyAsync(bytes, token); await connection.GetStream().WriteAsync(bytes, token);
            }, token);
            using (var direct = await Connect(mixed, IPAddress.Loopback, directPort, null, null, token))
            {
                await direct.GetStream().WriteAsync(new byte[321], token); var bytes = new byte[321]; await direct.GetStream().ReadExactlyAsync(bytes, token);
            }
            await directServe; await Task.Delay(80, token);
            Check(meter.Snapshot().Proxy == beforeDirect, "DIRECT rule bypasses proxy meter");
        }
        finally
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(output, error);
            if (Path.GetFullPath(directory).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(directory, true);
        }
    }
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static async Task Until(Func<bool> done, CancellationToken token)
    {
        var timer = Stopwatch.StartNew();
        while (!done()) { if (timer.Elapsed.TotalSeconds > 10) throw new TimeoutException("Relay condition timed out"); await Task.Delay(20, token); }
    }
    private static async Task Authenticate(Stream stream, string? user, string? password, CancellationToken token, bool expected = true)
    {
        await stream.WriteAsync(new byte[] { 5, 1, user is null ? (byte)0 : (byte)2 }, token);
        var response = new byte[2]; await stream.ReadExactlyAsync(response, token);
        if (user is null) { if (response[1] != 0) throw new IOException("No-auth refused"); return; }
        if (response[1] != 2) throw new IOException("Auth method refused");
        var u = Encoding.UTF8.GetBytes(user); var p = Encoding.UTF8.GetBytes(password!);
        var packet = new byte[3 + u.Length + p.Length]; packet[0] = 1; packet[1] = (byte)u.Length; u.CopyTo(packet, 2); packet[2 + u.Length] = (byte)p.Length; p.CopyTo(packet, 3 + u.Length);
        await stream.WriteAsync(packet, token); await stream.ReadExactlyAsync(response, token);
        if ((response[1] == 0) != expected) throw new IOException("Wrong authentication outcome");
    }
    private static async Task<TcpClient> Connect(int proxy, IPAddress ip, int port, string? user, string? password, CancellationToken token)
    {
        var client = new TcpClient { NoDelay = true };
        try
        {
            await client.ConnectAsync(IPAddress.Loopback, proxy, token); await Authenticate(client.GetStream(), user, password, token);
            var packet = Datagram(ip, port, []); packet[0] = 5; packet[1] = 1;
            await client.GetStream().WriteAsync(packet, token); await Reply(client.GetStream(), token); return client;
        }
        catch { client.Dispose(); throw; }
    }
    private static async Task<IPEndPoint> Reply(Stream stream, CancellationToken token)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        if (header[1] != 0) throw new IOException("SOCKS request failed: " + header[1]);
        int size = header[3] == 1 ? 4 : 16; var rest = new byte[size + 2]; await stream.ReadExactlyAsync(rest, token);
        var ip = new IPAddress(rest.AsSpan(0, size)); if (ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)) ip = IPAddress.Loopback;
        return new(ip, BinaryPrimitives.ReadUInt16BigEndian(rest.AsSpan(size)));
    }
    private static async Task TcpRoundtrip(int proxy, int echo, string? user, string? password, int count, CancellationToken token)
    {
        using var client = await Connect(proxy, IPAddress.Loopback, echo, user, password, token);
        var stream = client.GetStream();
        var payload = new byte[count]; Random.Shared.NextBytes(payload);
        await stream.WriteAsync(payload, token); client.Client.Shutdown(SocketShutdown.Send);
        using var result = new MemoryStream(); await stream.CopyToAsync(result, token);
        if (!result.ToArray().SequenceEqual(payload)) throw new Exception("TCP byte mismatch or half-close lost response");
    }
    private static byte[] Datagram(IPAddress address, int port, byte[] payload, bool domain = false)
    {
        var ip = domain ? Encoding.ASCII.GetBytes("localhost") : address.GetAddressBytes();
        int start = domain ? 5 : 4; var bytes = new byte[start + ip.Length + 2 + payload.Length];
        bytes[3] = domain ? (byte)3 : ip.Length == 4 ? (byte)1 : (byte)4; if (domain) bytes[4] = (byte)ip.Length;
        ip.CopyTo(bytes, start); BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(start + ip.Length), (ushort)port); payload.CopyTo(bytes, start + ip.Length + 2); return bytes;
    }
    private sealed class UdpAssociation(TcpClient control, UdpClient client, IPEndPoint server) : IAsyncDisposable
    {
        public IPEndPoint Server => server;
        public static async Task<UdpAssociation> Open(int port, string? user, string? password, CancellationToken token)
        {
            var control = new TcpClient(); await control.ConnectAsync(IPAddress.Loopback, port, token); await Authenticate(control.GetStream(), user, password, token);
            var packet = Datagram(IPAddress.Any, 0, []); packet[0] = 5; packet[1] = 3;
            await control.GetStream().WriteAsync(packet, token); var endpoint = await Reply(control.GetStream(), token);
            return new(control, new UdpClient(new IPEndPoint(IPAddress.Loopback, 0)), endpoint);
        }
        public async Task Roundtrip(int port, byte[] bytes, CancellationToken token, bool domain = false, bool ipv6 = false)
        {
            var datagram = Datagram(ipv6 ? IPAddress.IPv6Loopback : IPAddress.Loopback, port, bytes, domain);
            await client.SendAsync(datagram, server, token); var response = await client.ReceiveAsync(token);
            int offset = response.Buffer[3] == 1 ? 10 : 22;
            if (!response.Buffer.AsSpan(offset).SequenceEqual(bytes)) throw new Exception("UDP payload mismatch");
        }
        public async Task Fragment(int port, CancellationToken token)
        {
            var packet = Datagram(IPAddress.Loopback, port, new byte[111]); packet[2] = 1; await client.SendAsync(packet, server, token);
        }
        public ValueTask DisposeAsync() { control.Dispose(); client.Dispose(); return ValueTask.CompletedTask; }
    }
    private sealed class EchoServers : IAsyncDisposable
    {
        private readonly TcpListener tcp = new(IPAddress.Loopback, 0);
        private readonly UdpClient udp = new(new IPEndPoint(IPAddress.Loopback, 0));
        private readonly UdpClient? udp6 = Socket.OSSupportsIPv6 ? new(new IPEndPoint(IPAddress.IPv6Loopback, 0)) : null;
        private readonly CancellationTokenSource stop = new();
        private readonly Task[] loops;
        public int TcpPort => ((IPEndPoint)tcp.LocalEndpoint).Port;
        public int UdpPort => ((IPEndPoint)udp.Client.LocalEndPoint!).Port;
        public int Udp6Port => udp6 is null ? 0 : ((IPEndPoint)udp6.Client.LocalEndPoint!).Port;
        public EchoServers() { tcp.Start(); loops = udp6 is null ? [Tcp(), Udp(udp)] : [Tcp(), Udp(udp), Udp(udp6)]; }
        private async Task Tcp()
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    var client = await tcp.AcceptTcpClientAsync(stop.Token);
                    _ = Task.Run(async () =>
                    {
                        using (client)
                        {
                            try { var stream = client.GetStream(); using var body = new MemoryStream(); await stream.CopyToAsync(body, stop.Token); await stream.WriteAsync(body.ToArray(), stop.Token); }
                            catch (Exception ex) when (ex is IOException or OperationCanceledException) { }
                        }
                    });
                }
            }
            catch (OperationCanceledException) { }
            catch (SocketException) when (stop.IsCancellationRequested) { }
        }
        private async Task Udp(UdpClient server)
        {
            try { while (!stop.IsCancellationRequested) { var packet = await server.ReceiveAsync(stop.Token); await server.SendAsync(packet.Buffer, packet.RemoteEndPoint, stop.Token); } }
            catch (OperationCanceledException) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); tcp.Stop(); await Task.WhenAll(loops); udp.Dispose(); udp6?.Dispose(); stop.Dispose(); }
    }
}
