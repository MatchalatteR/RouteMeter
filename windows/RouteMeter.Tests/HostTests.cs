using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using RouteMeter.Windows;
using RouteMeter.Core;

internal static class HostTests
{
    public static async Task<int> Run(string executable)
    {
        var folder = Path.Combine(Path.GetTempPath(), "RouteMeter-host-test-" + Guid.NewGuid().ToString("N"));
        Storage.Folder = folder; var options = RelayOptions.Load();
        using (var free = new TcpListener(IPAddress.Loopback, 0)) { free.Start(); options.Port = ((IPEndPoint)free.LocalEndpoint).Port; }
        Storage.Save("relay.json", options);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        Process Start()
        {
            var info = new ProcessStartInfo(Path.GetFullPath(executable)) { UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("--relay"); info.ArgumentList.Add("--data-dir"); info.ArgumentList.Add(folder);
            return Process.Start(info)!;
        }
        async Task<RelaySnapshot> Ready()
        {
            while (true)
            {
                try { return await RelayRuntime.Request("snapshot", token); }
                catch (Exception ex) when ((ex is IOException or OperationCanceledException) && !token.IsCancellationRequested) { await Task.Delay(50, token); }
            }
        }
        void Check(bool ok, string text) { if (!ok) throw new Exception("FAIL host: " + text); }
        Process? process = null;
        try
        {
            process = Start(); var first = await Ready(); Check(first.Proxy == new Bytes(), "fresh host");
            await RelayTests.HostRoundtrip(options.Port, options.User, SecretProtection.Unprotect(options.ProtectedPassword), token);
            var counted = await RelayRuntime.Request("snapshot", token);
            Check(counted.Proxy == new Bytes(45678, 45678), "out-of-process byte ledger");
            var snapshots = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => RelayRuntime.Request("snapshot", token)));
            Check(snapshots.All(x => x.Proxy == counted.Proxy), "parallel private IPC snapshots");
            await RelayRuntime.Request("stop", token); await process.WaitForExitAsync(token); Check(process.ExitCode == 0, "normal checkpoint and shutdown"); process.Dispose();
            process = Start(); var resumed = await Ready();
            Check(resumed.Proxy == counted.Proxy && resumed.Started == counted.Started && resumed.Session != counted.Session, "restart preserves counters and changes process session");
            var reset = await RelayRuntime.Request("reset", token); Check(reset.Proxy == new Bytes(), "IPC reset");
            await RelayRuntime.Request("stop", token); await process.WaitForExitAsync(token);
            Check(Storage.Load<RelayLedger>("relay-state.json").Proxy == new Bytes(), "reset saved on disk");
            return 7;
        }
        finally
        {
            if (process is not null) { if (!process.HasExited) { process.Kill(true); await process.WaitForExitAsync(); } process.Dispose(); }
            if (Directory.Exists(folder) && Path.GetFullPath(folder).StartsWith(Path.GetFullPath(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)) Directory.Delete(folder, true);
        }
    }
}
