using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RouteMeter.Core;

namespace RouteMeter.Windows;

public sealed class RelayOptions
{
    public int Port { get; set; } = 18765;
    public string User { get; set; } = "routemeter";
    public string ProtectedPassword { get; set; } = "";
    public static RelayOptions Load()
    {
        var options = Storage.Load<RelayOptions>("relay.json");
        if (options.Port is < 1024 or > 65535 || options.User is null || options.User.Length is < 1 or > 255 || options.ProtectedPassword is null) throw new IOException("计量转发器配置无效");
        if (options.ProtectedPassword.Length == 0)
        {
            options.ProtectedPassword = SecretProtection.Protect(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            Storage.Save("relay.json", options);
        }
        return options;
    }
}

public static class RelayRuntime
{
    private static readonly JsonSerializerOptions json = new() { PropertyNameCaseInsensitive = true };
    public static string Identity => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(Storage.Folder).ToUpperInvariant())))[..16];
    public static string PipeName => "RouteMeter.Relay." + Identity;
    public static async Task<RelaySnapshot> Request(string command, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var pipe = new NamedPipeClientStream(".", PipeName, PipeDirection.InOut, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        await pipe.ConnectAsync(timeout.Token);
        await pipe.WriteAsync(Encoding.ASCII.GetBytes(command + "\n"), timeout.Token);
        var response = await ReadLine(pipe, 8192, timeout.Token);
        return JsonSerializer.Deserialize<RelaySnapshot>(response, json) ?? throw new JsonException();
    }
    private static async Task<string> ReadLine(Stream stream, int limit, CancellationToken token)
    {
        var b = new byte[1]; using var text = new MemoryStream();
        while (true)
        {
            if (await stream.ReadAsync(b, token) == 0) throw new EndOfStreamException();
            if (b[0] == 10) break;
            if (text.Length >= limit) throw new IOException("IPC message too large"); text.WriteByte(b[0]);
        }
        return Encoding.UTF8.GetString(text.ToArray());
    }
    public static void StartHost()
    {
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden };
        start.ArgumentList.Add("--relay"); start.ArgumentList.Add("--data-dir"); start.ArgumentList.Add(Storage.Folder);
        using var process = Process.Start(start);
    }
    public static async Task<int> RunHost()
    {
        using var mutex = new Mutex(true, @"Local\RouteMeter.Relay." + Identity, out bool first);
        if (!first) return 0;
        try
        {
            var options = RelayOptions.Load();
            var ledger = Storage.Load<RelayLedger>("relay-state.json");
            if (ledger.Version != 1) throw new IOException("Unsupported relay ledger");
            await using var relay = new SocksRelay(options.Port, options.User, SecretProtection.Unprotect(options.ProtectedPassword), ledger);
            relay.Start();
            using var stop = new CancellationTokenSource();
            var handlers = new ConcurrentDictionary<long, Task>();
            long nextRequest = 0;
            using var connections = new SemaphoreSlim(8);
            var saveGate = new object();
            string? saveError = null;
            void Save()
            {
                lock (saveGate)
                {
                    try { Storage.Save("relay-state.json", relay.Checkpoint()); saveError = null; }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { saveError = "代理计数暂时无法写入磁盘，请检查可用空间。"; }
                }
            }
            async Task Checkpoints()
            {
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        await Task.Delay(2000, stop.Token);
                        Save();
                    }
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
            var checkpoints = Checkpoints();
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    await connections.WaitAsync(stop.Token);
                    var pipe = new NamedPipeServerStream(PipeName, PipeDirection.InOut, 8, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                    try { await pipe.WaitForConnectionAsync(stop.Token); }
                    catch { pipe.Dispose(); connections.Release(); throw; }
                    var handler = Task.Run(async () =>
                    {
                        try
                        {
                            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
                            timeout.CancelAfter(TimeSpan.FromSeconds(3));
                            var command = await ReadLine(pipe, 64, timeout.Token);
                            if (command == "reset") { lock (saveGate) { relay.Reset(); Save(); } }
                            else if (command != "snapshot" && command != "stop") return;
                            await pipe.WriteAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(relay.Snapshot() with { Notice = saveError }) + "\n"), timeout.Token);
                            await pipe.FlushAsync(timeout.Token);
                            if (command == "stop") stop.Cancel();
                        }
                        catch (Exception ex) when (ex is IOException or OperationCanceledException or UnauthorizedAccessException) { }
                        finally { pipe.Dispose(); connections.Release(); }
                    });
                    handlers[Interlocked.Increment(ref nextRequest)] = handler;
                    // Keep only bounded live request tasks for shutdown.
                    if (handlers.Count > 32) foreach (var pair in handlers) if (pair.Value.IsCompleted) handlers.TryRemove(pair.Key, out _);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            finally
            {
                stop.Cancel(); await checkpoints; await Task.WhenAll(handlers.Values);
                await relay.DisposeAsync(); Save();
            }
            return 0;
        }
        catch (Exception ex) when (ex is IOException or System.Net.Sockets.SocketException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or JsonException)
        {
            try { Directory.CreateDirectory(Storage.Folder); File.WriteAllText(Path.Combine(Storage.Folder, "relay-error.txt"), "Relay startup/save failed: " + ex.GetType().Name); } catch (IOException) { }
            return 1;
        }
    }
    public static string ProfileScript(RelayOptions options)
    {
        using var stream = typeof(RelayRuntime).Assembly.GetManifestResourceStream("RouteMeter.Windows.relay-profile.js") ?? throw new IOException("Missing profile template");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("__ROUTEMETER_PORT__", options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("__ROUTEMETER_USER__", JsonSerializer.Serialize(options.User), StringComparison.Ordinal)
            .Replace("__ROUTEMETER_PASSWORD__", JsonSerializer.Serialize(SecretProtection.Unprotect(options.ProtectedPassword)), StringComparison.Ordinal);
    }
}
