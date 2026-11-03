using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RouteMeter.Windows;
public sealed class Counts
{
    [JsonRequired] public ulong Download { get; init; }
    [JsonRequired] public ulong Upload { get; init; }
    public ulong Total => Add(Download, Upload);
    public static ulong Add(ulong a, ulong b) => a > ulong.MaxValue - b ? ulong.MaxValue : a + b;
    public static Counts Sum(Counts a, Counts b) => new() { Download = Add(a.Download,b.Download), Upload = Add(a.Upload,b.Upload) };
    public static Counts Difference(Counts a, Counts b) => new() { Download = a.Download >= b.Download ? a.Download-b.Download : 0, Upload = a.Upload >= b.Upload ? a.Upload-b.Upload : 0 };
}
public sealed class Routes
{
    [JsonRequired] public Counts Direct { get; init; } = new();
    [JsonRequired] public Counts Proxy { get; init; } = new();
    [JsonRequired] public Counts Unknown { get; init; } = new();
    public Counts Total => Counts.Sum(Counts.Sum(Direct,Proxy),Unknown);
}
public sealed class CoreApp
{
    [JsonRequired] public string Id { get; init; } = "";
    [JsonRequired] public string Name { get; init; } = "";
    public string? Executable { get; init; }
    [JsonRequired] public string Attribution { get; init; } = "";
    [JsonRequired] public Routes Usage { get; init; } = new();
    [JsonRequired] public ulong Active { get; init; }
}
public sealed class CoreSample
{
    [JsonRequired] public int Schema { get; init; }
    [JsonRequired] public string Program { get; init; } = "";
    [JsonRequired] public string Epoch { get; init; } = "";
    [JsonRequired] public string Instance { get; init; } = "";
    [JsonRequired] public long Since { get; init; }
    [JsonRequired] public long Timestamp { get; init; }
    [JsonRequired] public ulong Sequence { get; init; }
    [JsonRequired] public Routes Totals { get; init; } = new();
    [JsonRequired] public List<CoreApp> Apps { get; init; } = [];
    [JsonRequired] public ulong UncertainUpload { get; init; }
    [JsonRequired] public bool Overflow { get; init; }
    public string? PersistenceError { get; init; }
    [JsonRequired] public string Measurement { get; init; } = "";
    [JsonRequired] public long Checkpoint { get; init; }
    public void Validate()
    {
        if (Schema!=2 || Program!="RouteMeterCore" || Measurement!="routing-core-payload-lifecycle" ||
            string.IsNullOrEmpty(Epoch) || string.IsNullOrEmpty(Instance) || Apps is null || Apps.Count>4097 ||
            Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()-(decimal)Timestamp)>30000 ||
            Apps.Any(a=>a is null || string.IsNullOrEmpty(a.Id) || string.IsNullOrEmpty(a.Name) || a.Usage is null || a.Usage.Direct is null || a.Usage.Proxy is null || a.Usage.Unknown is null) ||
            Totals is null || Totals.Direct is null || Totals.Proxy is null || Totals.Unknown is null || Apps.Select(a=>a.Id).Distinct().Count()!=Apps.Count) throw new InvalidDataException("Invalid meter snapshot");
        foreach (var route in new[] { "direct", "proxy", "unknown" })
        {
            Counts Get(Routes r) => route=="direct"?r.Direct:route=="proxy"?r.Proxy:r.Unknown;
            var sum=Apps.Aggregate(new Counts(),(count,app)=>Counts.Sum(count,Get(app.Usage)));
            var actual=Get(Totals);
            if (sum.Download!=actual.Download || sum.Upload!=actual.Upload) throw new InvalidDataException("Inconsistent totals");
        }
    }
}
public sealed class Connection
{
    [JsonRequired] public int Schema { get; init; }
    [JsonRequired] public string Endpoint { get; init; } = "";
    [JsonRequired] public string Token { get; init; } = "";
    public Uri Validate()
    {
        if (Schema!=1 || Token.Length!=64 || Token.Any(c=>!"0123456789abcdef".Contains(c)) ||
            !Uri.TryCreate(Endpoint,UriKind.Absolute,out var uri) || uri.Scheme!="http" ||
            (uri.Host!="127.0.0.1" && uri.Host!="[::1]" && uri.Host!="::1") || uri.Port<=0 ||
            uri.UserInfo.Length>0 || uri.Query.Length>0 || uri.Fragment.Length>0 || uri.AbsolutePath!="/")
            throw new InvalidDataException("Invalid loopback connection file");
        return new Uri(uri,"/v1/usage");
    }
}
public sealed class CoreClient : IDisposable
{
    public static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive=true };
    private readonly HttpClient client = new(new HttpClientHandler { UseProxy=false, AllowAutoRedirect=false })
    { Timeout=TimeSpan.FromSeconds(3), MaxResponseContentBufferSize=32_000_000 };
    public async Task<CoreSample> Read(string file)
    {
        if (new FileInfo(file).Length>65536) throw new InvalidDataException("Connection file too large");
        var connection=JsonSerializer.Deserialize<Connection>(await File.ReadAllTextAsync(file),Json) ?? throw new InvalidDataException();
        using var request=new HttpRequestMessage(HttpMethod.Get,connection.Validate());
        request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",connection.Token);
        using var response=await client.SendAsync(request);
        response.EnsureSuccessStatusCode();
        var sample=JsonSerializer.Deserialize<CoreSample>(await response.Content.ReadAsStringAsync(),Json) ?? throw new InvalidDataException();
        sample.Validate();return sample;
    }
    public void Dispose() => client.Dispose();
}
