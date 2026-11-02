using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;

namespace RouteMeter.Core;

public sealed class RelayLedger
{
    public int Version { get; set; } = 1;
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public Bytes Proxy { get; set; }
}
public sealed record RelaySnapshot(string Session, DateTimeOffset Started, Bytes Proxy, int TcpActive, int UdpActive, long Accepted, string? Notice = null);

/// <summary>Authenticated loopback SOCKS5 TCP/UDP forwarder. Counters belong to
/// forwarded transport bytes, not snapshots of currently open connections.</summary>
public sealed class SocksRelay : IAsyncDisposable
{
    private readonly TcpListener listener;
    private readonly byte[] username, password;
    private readonly CancellationTokenSource stop = new();
    private readonly SemaphoreSlim slots = new(512);
    private readonly ConcurrentDictionary<long, Task> sessions = new();
    private readonly object counterLock = new();
    private readonly string session = Guid.NewGuid().ToString("N");
    private RelayLedger ledger;
    private long nextSession, accepted;
    private int tcpActive, udpActive;
    private Task? accepting;
    private int disposed;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public SocksRelay(int port, string user, string secret, RelayLedger? saved = null)
    {
        username = Encoding.UTF8.GetBytes(user); password = Encoding.UTF8.GetBytes(secret);
        if (username.Length is < 1 or > 255 || password.Length is < 1 or > 255) throw new ArgumentException("Invalid SOCKS credentials");
        listener = new TcpListener(IPAddress.Loopback, port);
        ledger = saved is null ? new() : new() { Version = saved.Version, Started = saved.Started, Proxy = saved.Proxy };
    }
    public void Start() { listener.Start(128); accepting = Accept(); }
    public RelaySnapshot Snapshot()
    {
        lock (counterLock) return new(session, ledger.Started, ledger.Proxy, Volatile.Read(ref tcpActive), Volatile.Read(ref udpActive), Interlocked.Read(ref accepted));
    }
    public RelayLedger Checkpoint() { lock (counterLock) return new() { Started = ledger.Started, Proxy = ledger.Proxy }; }
    public void Reset() { lock (counterLock) ledger = new(); }
    private void Count(bool upload, int size)
    {
        if (size <= 0) return;
        lock (counterLock) ledger.Proxy += upload ? new Bytes(0, (ulong)size) : new Bytes((ulong)size, 0);
    }
    private async Task Accept()
    {
        try
        {
            while (!stop.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stop.Token);
                if (!slots.Wait(0)) { client.Dispose(); continue; }
                long id = Interlocked.Increment(ref nextSession);
                // Yield before adding to the dictionary so a synchronous refusal cannot
                // complete and remove itself before registration.
                var task = Task.Run(async () =>
                {
                    try { await Handle(client); }
                    finally { client.Dispose(); slots.Release(); sessions.TryRemove(id, out _); }
                });
                sessions[id] = task;
                if (task.IsCompleted) sessions.TryRemove(id, out _);
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    private async Task Handle(TcpClient client)
    {
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            using var handshake = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            handshake.CancelAfter(TimeSpan.FromSeconds(5));
            var token = handshake.Token;
            var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, token);
            if (greeting[0] != 5 || greeting[1] == 0) return;
            var methods = new byte[greeting[1]]; await stream.ReadExactlyAsync(methods, token);
            if (!methods.Contains((byte)2)) { await stream.WriteAsync(new byte[] { 5, 255 }, token); return; }
            await stream.WriteAsync(new byte[] { 5, 2 }, token);
            var header = new byte[2]; await stream.ReadExactlyAsync(header, token);
            if (header[0] != 1 || header[1] == 0) return;
            var user = new byte[header[1]]; await stream.ReadExactlyAsync(user, token);
            var length = new byte[1]; await stream.ReadExactlyAsync(length, token);
            var pass = new byte[length[0]]; await stream.ReadExactlyAsync(pass, token);
            bool authorized = CryptographicOperations.FixedTimeEquals(user, username) & CryptographicOperations.FixedTimeEquals(pass, password);
            await stream.WriteAsync(new byte[] { 1, authorized ? (byte)0 : (byte)1 }, token);
            if (!authorized) return;
            handshake.CancelAfter(TimeSpan.FromSeconds(15));
            var request = new byte[4]; await stream.ReadExactlyAsync(request, token);
            if (request[0] != 5 || request[2] != 0) return;
            var target = await ReadAddress(stream, request[3], token);
            if (request[1] == 1)
            {
                using var remote = new TcpClient { NoDelay = true };
                try { await remote.ConnectAsync(target.Host, target.Port, token); }
                catch (Exception ex) when (ex is SocketException or OperationCanceledException)
                { await Reply(stream, 5, IPAddress.Loopback, 0, stop.Token); return; }
                await Reply(stream, 0, IPAddress.Loopback, 0, token);
                Interlocked.Increment(ref accepted); Interlocked.Increment(ref tcpActive);
                try { await ForwardTcp(client, remote); }
                finally { Interlocked.Decrement(ref tcpActive); }
            }
            else if (request[1] == 3)
            {
                await AssociateUdp(client, target, token);
            }
            else await Reply(stream, 7, IPAddress.Loopback, 0, token);
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ArgumentException or ObjectDisposedException) { }
    }
    private async Task ForwardTcp(TcpClient client, TcpClient remote)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var clientStream = client.GetStream();
        var remoteStream = remote.GetStream();
        async Task Pump(Stream source, TcpClient destination, bool upload)
        {
            try
            {
                var buffer = new byte[32768]; int n;
                while ((n = await source.ReadAsync(buffer, lifetime.Token)) > 0)
                {
                    // Download counts bytes received from the node; upload counts
                    // bytes accepted by the outgoing socket. Handshake framing on
                    // this local SOCKS listener is deliberately excluded.
                    if (!upload) Count(false, n);
                    int offset = 0;
                    while (offset < n)
                    {
                        int sent = await destination.Client.SendAsync(buffer.AsMemory(offset, n - offset), SocketFlags.None, lifetime.Token);
                        if (sent == 0) throw new IOException("Socket closed during forwarding");
                        if (upload) Count(true, sent);
                        offset += sent;
                    }
                }
                try { destination.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
            }
            catch { lifetime.Cancel(); throw; }
        }
        await Task.WhenAll(Pump(clientStream, remote, true), Pump(remoteStream, client, false));
    }
    private async Task AssociateUdp(TcpClient client, Address requested, CancellationToken handshake)
    {
        using var local = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var remote4 = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        using var remote6 = Socket.OSSupportsIPv6 ? new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp) : null;
        local.Bind(new IPEndPoint(IPAddress.Loopback, 0)); remote4.Bind(new IPEndPoint(IPAddress.Any, 0));
        remote6?.Bind(new IPEndPoint(IPAddress.IPv6Any, 0));
        await Reply(client.GetStream(), 0, IPAddress.Loopback, ((IPEndPoint)local.LocalEndPoint!).Port, handshake);
        Interlocked.Increment(ref accepted); Interlocked.Increment(ref udpActive);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        var token = lifetime.Token;
        var gate = new object();
        IPEndPoint? peer = requested.Port == 0 ? null : new(IPAddress.Loopback, requested.Port);
        var allowed = new HashSet<IPEndPoint>();
        var dns = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        async Task FromLocal()
        {
            var buffer = new byte[65535];
            while (!token.IsCancellationRequested)
            {
                var result = await local.ReceiveFromAsync(buffer, SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), token);
                var sender = (IPEndPoint)result.RemoteEndPoint;
                if (!IPAddress.IsLoopback(sender.Address)) continue;
                lock (gate) { peer ??= sender; if (!sender.Equals(peer)) continue; }
                if (!TryDatagram(buffer.AsSpan(0, result.ReceivedBytes), out var address, out int offset)) continue;
                IPAddress ip;
                if (!IPAddress.TryParse(address.Host, out ip!))
                {
                    if (!dns.TryGetValue(address.Host, out ip!))
                    {
                        IPAddress[] ips;
                        try { ips = await Dns.GetHostAddressesAsync(address.Host, token); } catch (SocketException) { continue; }
                        ip = ips.FirstOrDefault(x => x.AddressFamily == AddressFamily.InterNetwork) ?? ips.FirstOrDefault()!;
                        if (ip is null) continue;
                        if (dns.Count >= 256) dns.Clear(); dns[address.Host] = ip;
                    }
                }
                var endpoint = new IPEndPoint(ip, address.Port);
                var outgoing = ip.AddressFamily == AddressFamily.InterNetworkV6 ? remote6 : remote4;
                if (outgoing is null) continue;
                lock (gate) { if (allowed.Count >= 4096 && !allowed.Contains(endpoint)) continue; allowed.Add(endpoint); }
                try
                {
                    int sent = await outgoing.SendToAsync(buffer.AsMemory(offset, result.ReceivedBytes - offset), SocketFlags.None, endpoint, token);
                    Count(true, sent);
                }
                catch (SocketException) { }
            }
        }
        async Task FromRemote(Socket socket, IPAddress wildcard)
        {
            var payload = new byte[65535];
            while (!token.IsCancellationRequested)
            {
                SocketReceiveFromResult result;
                try { result = await socket.ReceiveFromAsync(payload, SocketFlags.None, new IPEndPoint(wildcard, 0), token); }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.ConnectionReset) { continue; }
                var sender = (IPEndPoint)result.RemoteEndPoint;
                IPEndPoint? destination;
                lock (gate) { if (!allowed.Contains(sender)) continue; destination = peer; }
                if (destination is null) continue;
                Count(false, result.ReceivedBytes);
                var header = DatagramHeader(sender);
                if (header.Length + result.ReceivedBytes > 65507) continue;
                var datagram = new byte[header.Length + result.ReceivedBytes]; header.CopyTo(datagram, 0); payload.AsSpan(0, result.ReceivedBytes).CopyTo(datagram.AsSpan(header.Length));
                await local.SendToAsync(datagram, SocketFlags.None, destination, token);
            }
        }
        // RFC 1928 ties the UDP association lifetime to this TCP control connection.
        async Task Control()
        {
            var b = new byte[1]; while (await client.GetStream().ReadAsync(b, token) > 0) { }
        }
        Task[] tasks = remote6 is null ? [FromLocal(), FromRemote(remote4, IPAddress.Any), Control()] : [FromLocal(), FromRemote(remote4, IPAddress.Any), FromRemote(remote6, IPAddress.IPv6Any), Control()];
        try { await Task.WhenAny(tasks); }
        finally
        {
            lifetime.Cancel();
            try { await Task.WhenAll(tasks); } catch (Exception ex) when (ex is OperationCanceledException or SocketException or IOException or ObjectDisposedException) { }
            Interlocked.Decrement(ref udpActive);
        }
    }
    private readonly record struct Address(string Host, int Port);
    private static async Task<Address> ReadAddress(Stream stream, byte type, CancellationToken token)
    {
        int length;
        if (type == 1) length = 4;
        else if (type == 4) length = 16;
        else if (type == 3) { var b = new byte[1]; await stream.ReadExactlyAsync(b, token); length = b[0]; if (length == 0) throw new IOException("Empty host"); }
        else throw new IOException("Invalid address type");
        var address = new byte[length]; await stream.ReadExactlyAsync(address, token);
        var port = new byte[2]; await stream.ReadExactlyAsync(port, token);
        return new(type == 3 ? Encoding.ASCII.GetString(address) : new IPAddress(address).ToString(), BinaryPrimitives.ReadUInt16BigEndian(port));
    }
    private static Task Reply(Stream stream, byte status, IPAddress ip, int port, CancellationToken token)
    {
        var bytes = ip.GetAddressBytes(); var reply = new byte[6 + bytes.Length]; reply[0] = 5; reply[1] = status; reply[3] = bytes.Length == 4 ? (byte)1 : (byte)4;
        bytes.CopyTo(reply, 4); BinaryPrimitives.WriteUInt16BigEndian(reply.AsSpan(reply.Length - 2), (ushort)port);
        return stream.WriteAsync(reply, token).AsTask();
    }
    private static bool TryDatagram(ReadOnlySpan<byte> packet, out Address address, out int offset)
    {
        address = default; offset = 0;
        if (packet.Length < 4 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0) return false;
        int start = 4, size;
        if (packet[3] == 1) size = 4;
        else if (packet[3] == 4) size = 16;
        else if (packet[3] == 3) { if (packet.Length < 5 || packet[4] == 0) return false; size = packet[4]; start = 5; }
        else return false;
        offset = start + size + 2;
        if (packet.Length < offset) return false;
        var bytes = packet.Slice(start, size);
        string host = packet[3] == 3 ? Encoding.ASCII.GetString(bytes) : new IPAddress(bytes).ToString();
        int port = BinaryPrimitives.ReadUInt16BigEndian(packet.Slice(start + size, 2));
        if (port == 0) return false;
        address = new(host, port); return true;
    }
    private static byte[] DatagramHeader(IPEndPoint endpoint)
    {
        var address = endpoint.Address.GetAddressBytes(); var header = new byte[6 + address.Length]; header[3] = address.Length == 4 ? (byte)1 : (byte)4;
        address.CopyTo(header, 4); BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(header.Length - 2), (ushort)endpoint.Port); return header;
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stop.Cancel(); listener.Stop();
        if (accepting is not null) await accepting;
        await Task.WhenAll(sessions.Values);
        stop.Dispose(); slots.Dispose();
    }
}
