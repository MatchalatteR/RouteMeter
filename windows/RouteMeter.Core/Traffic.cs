using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouteMeter.Core;

public enum RouteKind { Unknown, Direct, Proxy }

public readonly record struct Bytes(ulong Down = 0, ulong Up = 0)
{
    public ulong Total => Add(Down, Up);
    public static ulong Add(ulong a, ulong b) => ulong.MaxValue - a < b ? ulong.MaxValue : a + b;
    public static Bytes operator +(Bytes a, Bytes b) => new(Add(a.Down, b.Down), Add(a.Up, b.Up));
    public static Bytes operator -(Bytes a, Bytes b) => new(a.Down - b.Down, a.Up - b.Up);
    public Bytes PerSecond(double seconds) => seconds > 0 ? new((ulong)Math.Min(ulong.MaxValue, Down / seconds), (ulong)Math.Min(ulong.MaxValue, Up / seconds)) : new();
    public static string Format(ulong value, bool rate = false)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB", "PB", "EB"];
        double n = value; int i = 0;
        while (n >= 1024 && i < units.Length - 1) { n /= 1024; i++; }
        return n.ToString(i == 0 || n >= 100 ? "0" : "0.0", System.Globalization.CultureInfo.InvariantCulture) + " " + units[i] + (rate ? "/s" : "");
    }
}

// Deliberately omit metadata (hosts, IP addresses, processes and rule payloads).
public sealed class Connection
{
    public string Id { get; set; } = "";
    public ulong Download { get; set; }
    public ulong Upload { get; set; }
    public string[] Chains { get; set; } = [];
}
public sealed class Snapshot
{
    public ulong? DownloadTotal { get; set; }
    public ulong? UploadTotal { get; set; }
    public Connection[]? Connections { get; set; }
    public static Snapshot Parse(string json)
    {
        var result = JsonSerializer.Deserialize<Snapshot>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true, MaxDepth = 32 }) ?? throw new JsonException("Empty snapshot");
        if (result.DownloadTotal is null || result.UploadTotal is null || result.Connections is null || result.Connections.Length > 20000)
            throw new JsonException("Invalid snapshot schema");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in result.Connections)
            if (c is null || string.IsNullOrEmpty(c.Id) || c.Id.Length > 256 || !ids.Add(c.Id) || c.Chains is null || c.Chains.Length > 64 || c.Chains.Any(x => x is null || x.Length > 4096))
                throw new JsonException("Invalid connection schema");
        return result;
    }
}

public sealed class Baseline
{
    public Bytes Bytes { get; set; }
}
public sealed class MeterState
{
    public int Version { get; set; } = 1;
    public DateTimeOffset Started { get; set; } = DateTimeOffset.Now;
    public Bytes Direct { get; set; }
    public Bytes Proxy { get; set; }
    public Bytes Unknown { get; set; }
    public Bytes? PreviousTotal { get; set; }
    public Dictionary<string, Baseline> Previous { get; set; } = new(StringComparer.Ordinal);
    public string SourceKey { get; set; } = "";
    [JsonIgnore] public Bytes Total => Direct + Proxy + Unknown;
}
public readonly record struct Sample(Bytes Direct, Bytes Proxy, Bytes Unknown, double Seconds, bool Rebased)
{
    public Bytes Total => Direct + Proxy + Unknown;
}

public sealed class TrafficAccumulator(MeterState state)
{
    public MeterState State { get; } = state;
    private double? previousTime;
    public static RouteKind Classify(string[] chains, IReadOnlyDictionary<string, string> types)
    {
        // Mihomo appends adapters from the leaf toward the policy group.
        if (chains.Length == 0) return RouteKind.Unknown;
        var leaf = chains[0];
        var type = types.TryGetValue(leaf, out var found) ? found : leaf;
        if (type.Equals("Direct", StringComparison.OrdinalIgnoreCase) || type.Equals("Compatible", StringComparison.OrdinalIgnoreCase)) return RouteKind.Direct;
        if (type.StartsWith("Reject", StringComparison.OrdinalIgnoreCase) || type.Equals("Pass", StringComparison.OrdinalIgnoreCase)) return RouteKind.Unknown;
        // A policy name alone does not prove which adapter carried the bytes.
        if (!types.ContainsKey(leaf) || new[] { "Selector", "URLTest", "Fallback", "LoadBalance", "Relay" }.Contains(type, StringComparer.OrdinalIgnoreCase)) return RouteKind.Unknown;
        return RouteKind.Proxy;
    }

    public Sample Ingest(Snapshot snapshot, IReadOnlyDictionary<string, string> types, double monotonicSeconds, string sourceKey)
    {
        var total = new Bytes(snapshot.DownloadTotal!.Value, snapshot.UploadTotal!.Value);
        var previous = State.PreviousTotal;
        bool rebase = previous is null || State.SourceKey != sourceKey || total.Down < previous.Value.Down || total.Up < previous.Value.Up;
        var seconds = previousTime is null ? 0 : Math.Max(0, monotonicSeconds - previousTime.Value);
        Bytes direct = new(), proxy = new(), unknown = new();
        if (!rebase)
        {
            var delta = total - previous!.Value;
            foreach (var c in snapshot.Connections!)
            {
                var current = new Bytes(c.Download, c.Upload);
                var old = State.Previous.TryGetValue(c.Id, out var baseline) ? baseline.Bytes : new Bytes();
                // Reused/reset connection counters have no recoverable classified delta.
                if (current.Down < old.Down || current.Up < old.Up) continue;
                var change = current - old;
                switch (Classify(c.Chains, types))
                {
                    case RouteKind.Direct: direct += change; break;
                    case RouteKind.Proxy: proxy += change; break;
                }
            }
            // Global and per-connection counters are sampled at slightly different instants.
            // Bound classified bytes by the actual global delta; never invent negative residuals.
            var down = Fit(direct.Down, proxy.Down, delta.Down);
            var up = Fit(direct.Up, proxy.Up, delta.Up);
            direct = new(down.Direct, up.Direct); proxy = new(down.Proxy, up.Proxy);
            unknown = new(delta.Down - direct.Down - proxy.Down, delta.Up - direct.Up - proxy.Up);
            State.Direct += direct; State.Proxy += proxy; State.Unknown += unknown;
        }
        State.PreviousTotal = total;
        State.Previous = snapshot.Connections!.ToDictionary(c => c.Id, c => new Baseline { Bytes = new(c.Download, c.Upload) }, StringComparer.Ordinal);
        State.SourceKey = sourceKey;
        previousTime = monotonicSeconds;
        return new(direct, proxy, unknown, seconds, rebase);
    }
    private static (ulong Direct, ulong Proxy) Fit(ulong direct, ulong proxy, ulong budget)
    {
        decimal sum = (decimal)direct + proxy;
        if (sum <= budget) return (direct, proxy);
        if (sum == 0) return (0, 0);
        var fittedDirect = (ulong)(budget * (decimal)direct / sum);
        return (fittedDirect, budget - fittedDirect);
    }
    public void Reset()
    {
        State.Started = DateTimeOffset.Now;
        State.Direct = State.Proxy = State.Unknown = new();
        Rebase();
    }
    public void Rebase() { State.PreviousTotal = null; State.Previous.Clear(); previousTime = null; }
    public void Disconnected() => previousTime = null;
}
