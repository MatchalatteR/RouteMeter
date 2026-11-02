using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using RouteMeter.Core;

namespace RouteMeter.Windows;

public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        string? Option(string flag) { int i = Array.IndexOf(args, flag); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        if (Option("--data-dir") is string data) Storage.Folder = Path.GetFullPath(data);
        if (args.Contains("--relay")) return RelayRuntime.RunHost().GetAwaiter().GetResult();
        if (args.Contains("--stop-relay"))
        {
            try { RelayRuntime.Request("stop", CancellationToken.None).GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) when (ex is IOException or OperationCanceledException) { return 1; }
        }
        if (args.Contains("--quit"))
        {
            if (EventWaitHandle.TryOpenExisting(@"Local\RouteMeter.Windows.Quit", out var quit)) { using (quit) quit.Set(); }
            return 0;
        }
        if (Option("--probe") is string report)
        {
            try { Probe(report, int.TryParse(Option("--samples"), out int n) ? Math.Clamp(n, 2, 30) : 4).GetAwaiter().GetResult(); return 0; }
            catch (Exception ex) { File.WriteAllText(report, JsonSerializer.Serialize(new { ok = false, error = ex.GetType().Name })); return 1; }
        }
        using var mutex = new Mutex(true, @"Local\RouteMeter.Windows", out bool first);
        using var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\RouteMeter.Windows.Show");
        using var quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\RouteMeter.Windows.Quit");
        if (!first) { showEvent.Set(); return 0; }
        var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        Theme.Install(application);
        var meter = new MeterService();
        var shell = new MeterShell(meter);
        var registration = ThreadPool.RegisterWaitForSingleObject(showEvent, (_, _) => application.Dispatcher.BeginInvoke(shell.ShowMeter), null, Timeout.Infinite, false);
        var quitRegistration = ThreadPool.RegisterWaitForSingleObject(quitEvent, (_, _) => application.Dispatcher.BeginInvoke(() => application.Shutdown()), null, Timeout.Infinite, false);
        application.Startup += (_, _) =>
        {
            if (meter.Settings.TaskbarVisible) shell.ShowMeter();
            _ = meter.Run();
            if (Option("--ui-report") is string reportPath)
            {
                var reportTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
                reportTimer.Tick += (_, _) => { reportTimer.Stop(); File.WriteAllText(reportPath, JsonSerializer.Serialize(new { taskbar = shell.Taskbar.Diagnostics(), live = meter.Live, sampleSeconds = meter.Current.Seconds }, new JsonSerializerOptions { WriteIndented = true })); };
                reportTimer.Start();
            }
            if (args.Contains("--details")) shell.ShowDetails();
        };
        application.Exit += (_, _) => { registration.Unregister(null); quitRegistration.Unregister(null); meter.Stop(); shell.Dispose(); };
        application.DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show("RouteMeter 遇到错误：" + e.Exception.GetType().Name + "。请重新启动。", "RouteMeter");
            e.Handled = true;
            application.Shutdown(1);
        };
        application.Run();
        return 0;
    }
    private static async Task Probe(string report, int count)
    {
        RelaySnapshot? snapshot = null;
        for (int i = 0; i < count; i++)
        {
            snapshot = await RelayRuntime.Request("snapshot", CancellationToken.None);
            if (i < count - 1) await Task.Delay(500);
        }
        File.WriteAllText(report, JsonSerializer.Serialize(new { ok = true, backend = "authenticated-loopback-socks5", samples = count, snapshot }, new JsonSerializerOptions { WriteIndented = true }));
    }

}

public sealed class MeterService
{
    public Settings Settings { get; } = Storage.Load<Settings>("settings.json");
    public RelayOptions RelayOptions { get; } = RelayOptions.Load();
    public TrafficAccumulator Accumulator { get; } = new(Storage.Load<MeterState>("direct-reference.json"));
    public RelaySnapshot? Relay { get; private set; }
    public Bytes ProxyTotal => Relay?.Proxy ?? new();
    public Bytes TotalVolume => Accumulator.State.Direct + ProxyTotal;
    public DateTimeOffset Started => Relay?.Started ?? Accumulator.State.Started;
    public Sample Current { get; private set; }
    public bool Live { get; private set; }
    public bool DirectLive { get; private set; }
    public string Status { get; private set; } = "正在启动本机计量转发器…";
    public string SourceLabel => "独立本机计量 · TCP / UDP";
    public int ConnectionCount => (Relay?.TcpActive ?? 0) + (Relay?.UdpActive ?? 0);
    public string? Notice { get; private set; } = Storage.Notice;
    public string? TaskbarNotice { get; set; }
    public string? CoverageWarning { get; private set; }
    public List<Sample?> History { get; } = [];
    public event Action? Changed;
    private readonly CancellationTokenSource stopping = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private double directAt = -100, savedAt, lastHostStart = -100;
    private Bytes directRate;
    private RelaySnapshot? previousRelay;
    private double previousRelayAt;
    private MihomoClient? client;
    private bool reconnect;
    private ProxyCoverage coverage = new(false, 0, 0);
    public async Task Run()
    {
        var directTask = ReadDirect();
        RelayRuntime.StartHost(); lastHostStart = clock.Elapsed.TotalSeconds;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                var snapshot = await RelayRuntime.Request("snapshot", stopping.Token);
                double now = clock.Elapsed.TotalSeconds;
                Bytes proxyRate = new();
                if (previousRelay is not null && snapshot.Session == previousRelay.Session && snapshot.Started == previousRelay.Started && snapshot.Proxy.Down >= previousRelay.Proxy.Down && snapshot.Proxy.Up >= previousRelay.Proxy.Up)
                    proxyRate = (snapshot.Proxy - previousRelay.Proxy).PerSecond(now - previousRelayAt);
                previousRelay = snapshot; previousRelayAt = now; Relay = snapshot;
                DirectLive = now - directAt < 3;
                // UI samples hold rates, with a one-second unit interval.
                Current = new(DirectLive ? directRate : new(), proxyRate, new(), 1, false);
                Live = snapshot.Accepted > 0 && coverage.Complete;
                Status = Live ? "代理实测 · 直连参考" : coverage.HasMeter && !coverage.Complete ? "存在未接入节点 · 请检查计量脚本" : "计量器就绪 · 等待接入 Clash Verge";
                History.Add(Live ? Current : null); if (History.Count > 120) History.RemoveAt(0);
                if (now - savedAt > 10) { Save(); savedAt = now; }
                Changed?.Invoke();
                await Task.Delay(500, stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is IOException or JsonException or OperationCanceledException or UnauthorizedAccessException)
            {
                Live = false; Current = default; previousRelay = null;
                Status = "计量转发器暂不可用 · 正在重试";
                Changed?.Invoke();
                if (clock.Elapsed.TotalSeconds - lastHostStart > 10) { RelayRuntime.StartHost(); lastHostStart = clock.Elapsed.TotalSeconds; }
                try { await Task.Delay(1000, stopping.Token); } catch (OperationCanceledException) { break; }
            }
        }
        await directTask;
    }
    private async Task ReadDirect()
    {
        Dictionary<string, string> types = new(StringComparer.Ordinal); double typesAt = -100;
        while (!stopping.IsCancellationRequested)
        {
            try
            {
                if (reconnect) { client?.Dispose(); client = null; typesAt = -100; reconnect = false; }
                client ??= new(Settings.Automatic ? ApiSource.Discover() : ApiSource.Manual(Settings.Endpoint, SecretProtection.Unprotect(Settings.ProtectedSecret)));
                if (clock.Elapsed.TotalSeconds - typesAt > 5)
                {
                    types = await client.ProxyTypes(stopping.Token); coverage = client.Coverage; typesAt = clock.Elapsed.TotalSeconds;
                    CoverageWarning = coverage.HasMeter && !coverage.Complete ? "有 " + coverage.Unmetered + " 个代理节点未接入计量转发器。请将计量脚本放在其他节点修改之后，重新加载配置。" : null;
                }
                var snapshot = await client.Connections(stopping.Token);
                if (reconnect) continue;
                var sample = Accumulator.Ingest(snapshot, types, clock.Elapsed.TotalSeconds, client.Source.Key);
                directRate = sample.Direct.PerSecond(sample.Seconds); directAt = clock.Elapsed.TotalSeconds;
                await Task.Delay(500, stopping.Token);
            }
            catch (OperationCanceledException) when (stopping.IsCancellationRequested) { break; }
            catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or JsonException or OperationCanceledException or ArgumentException or System.ComponentModel.Win32Exception or FormatException or InvalidOperationException)
            {
                client?.Dispose(); client = null; typesAt = -100; directAt = -100; coverage = new(false, 0, 0); Accumulator.Disconnected();
                try { await Task.Delay(2500, stopping.Token); } catch (OperationCanceledException) { break; }
            }
        }
    }
    public void Reconnect() { reconnect = true; directAt = -100; }
    public async Task Reset()
    {
        try
        {
            Relay = await RelayRuntime.Request("reset", stopping.Token);
            Accumulator.Reset(); previousRelay = null; Current = default; History.Clear(); Save(); Changed?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException) { Notice = "无法重置后台计量器，请稍后重试。"; Changed?.Invoke(); }
    }
    public void Save()
    {
        try { Storage.Save("direct-reference.json", Accumulator.State); Storage.Save("settings.json", Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Notice = "数据暂时无法保存，请检查本机磁盘空间和目录权限。"; }
    }
    public void Stop() { stopping.Cancel(); client?.Dispose(); Save(); }
}
