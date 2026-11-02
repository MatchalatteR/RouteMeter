using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using RouteMeter.Core;

namespace RouteMeter.Windows;

public sealed record ApiSource(string Endpoint, string Secret, string Label)
{
    public bool IsPipe => Endpoint.StartsWith("pipe:", StringComparison.Ordinal);
    public string Key => Endpoint;
    public static ApiSource Manual(string endpoint, string secret)
    {
        endpoint = endpoint.Trim();
        if (endpoint.StartsWith("pipe:", StringComparison.Ordinal))
        {
            var name = endpoint[5..];
            if (name.Length == 0 || name.Length > 240 || name.Contains('\\') || name.Contains('/')) throw new ArgumentException("请输入本机命名管道名称：pipe:名称");
            return new(endpoint, "", "本机命名管道");
        }
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != "http" || !new[] { "localhost", "127.0.0.1", "[::1]", "::1" }.Contains(uri.Host, StringComparer.OrdinalIgnoreCase) || uri.AbsolutePath != "/" || uri.Query != "" || uri.Fragment != "" || uri.UserInfo != "")
            throw new ArgumentException("仅支持本机地址，例如 http://127.0.0.1:9090");
        if (secret.Contains('\r') || secret.Contains('\n')) throw new ArgumentException("API 密钥格式无效");
        return new(uri.GetLeftPart(UriPartial.Authority), secret, "本机 HTTP API");
    }

    public static ApiSource Discover()
    {
        // Verge releases use different service/sidecar pipe names. A generated YAML
        // can describe a stopped sidecar while the production service owns the API.
        string[] activePipes;
        try { activePipes = Directory.GetFiles(@"\\.\pipe\").Where(x => x.StartsWith(@"\\.\pipe\verge-mihomo-", StringComparison.OrdinalIgnoreCase)).ToArray(); }
        catch (IOException) { activePipes = []; }
        ApiSource? httpFallback = null;
        var root = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var folders = new[] { "io.github.clash-verge-rev.clash-verge-rev", "io.github.clash-verge-rev", "io.github.clash-verge", "clash-verge" };
        foreach (var folder in folders)
            foreach (var file in new[] { "clash-verge.yaml", "config.yaml", "clash.yaml" })
            {
                string path = Path.Combine(root, folder, file);
                if (!File.Exists(path)) continue;
                try
                {
                    var values = ReadTopLevel(path);
                    if (values.TryGetValue("external-controller-pipe", out var pipe) && pipe.StartsWith(@"\\.\pipe\", StringComparison.OrdinalIgnoreCase) && activePipes.Contains(pipe, StringComparer.OrdinalIgnoreCase))
                        return Manual("pipe:" + pipe[9..], "") with { Label = "Clash Verge · 本机管道" };
                    if (values.TryGetValue("external-controller", out var controller) && !string.IsNullOrWhiteSpace(controller))
                    {
                        if (controller.StartsWith("0.0.0.0:")) controller = "127.0.0.1:" + controller[8..];
                        httpFallback ??= Manual("http://" + controller, values.GetValueOrDefault("secret", "")) with { Label = "Clash Verge · 本机 API" };
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or JsonException) { }
            }
        if (activePipes.Length == 1) return Manual("pipe:" + activePipes[0][9..], "") with { Label = "Clash Verge · 本机管道" };
        if (httpFallback is not null) return httpFallback;
        throw new IOException("未找到可用接口。请启动 Clash Verge，或在连接设置中填写本机 API。");
    }
    // Read only three top-level scalar fields; never deserialize subscription contents.
    internal static Dictionary<string, string> ReadTopLevel(string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new IOException("配置文件过大");
        foreach (var line in File.ReadLines(path))
        {
            if (line.Length == 0 || char.IsWhiteSpace(line[0]) || line[0] == '#') continue;
            int colon = line.IndexOf(':'); if (colon < 0) continue;
            var key = line[..colon];
            if (key is not ("external-controller-pipe" or "external-controller" or "secret")) continue;
            var value = line[(colon + 1)..].Trim();
            if (value.StartsWith('"'))
            {
                int end = 1;
                while (end < value.Length) { if (value[end] == '\\') { end += 2; continue; } if (value[end] == '"') break; end++; }
                value = JsonSerializer.Deserialize<string>(value[..Math.Min(end + 1, value.Length)]) ?? "";
            }
            else if (value.StartsWith('\''))
            {
                var text = new StringBuilder();
                for (int i = 1; i < value.Length; i++)
                {
                    if (value[i] == '\'') { if (i + 1 < value.Length && value[i + 1] == '\'') { text.Append('\''); i++; continue; } break; }
                    text.Append(value[i]);
                }
                value = text.ToString();
            }
            else
            {
                int comment = value.IndexOf(" #", StringComparison.Ordinal);
                if (comment >= 0) value = value[..comment].TrimEnd();
                if (value is "null" or "~") value = "";
            }
            result[key] = value;
        }
        return result;
    }
}

public sealed class MihomoClient : IDisposable
{
    private readonly HttpClient http;
    public ApiSource Source { get; }
    public ProxyCoverage Coverage { get; private set; } = new(false, 0, 0);
    public MihomoClient(ApiSource source)
    {
        Source = source;
        var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, MaxResponseHeadersLength = 32, ConnectTimeout = TimeSpan.FromSeconds(2) };
        if (source.IsPipe)
            handler.ConnectCallback = async (_, token) =>
            {
                var pipe = new NamedPipeClientStream(".", source.Endpoint[5..], PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(token); return pipe; }
                catch { pipe.Dispose(); throw; }
            };
        http = new(handler) { BaseAddress = new Uri(source.IsPipe ? "http://localhost" : source.Endpoint), Timeout = TimeSpan.FromSeconds(3) };
        if (!source.IsPipe && source.Secret.Length > 0) http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", source.Secret);
    }
    private async Task<string> Get(string path, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        using var response = await http.GetAsync(path, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == HttpStatusCode.Unauthorized) throw new UnauthorizedAccessException("API 密钥不正确，请检查连接设置。");
        response.EnsureSuccessStatusCode();
        const int limit = 16 * 1024 * 1024;
        if (response.Content.Headers.ContentLength > limit) throw new IOException("API 响应过大");
        using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var buffer = new MemoryStream();
        var block = new byte[16384]; int n;
        while ((n = await stream.ReadAsync(block, timeout.Token)) > 0)
        {
            if (buffer.Length + n > limit) throw new IOException("API 响应过大");
            buffer.Write(block, 0, n);
        }
        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, checked((int)buffer.Length));
    }
    public async Task<Snapshot> Connections(CancellationToken token) => Snapshot.Parse(await Get("/connections", token));
    public async Task<Dictionary<string, string>> ProxyTypes(CancellationToken token)
    {
        var result = ReadProxyMap(await Get("/proxies", token));
        Coverage = result.Coverage; return result.Types;
    }
    public static (Dictionary<string, string> Types, ProxyCoverage Coverage) ReadProxyMap(string response)
    {
        using var json = JsonDocument.Parse(response);
        var types = new Dictionary<string, string>(StringComparer.Ordinal);
        bool hasMeter = false; int metered = 0, unmetered = 0;
        foreach (var p in json.RootElement.GetProperty("proxies").EnumerateObject())
        {
            if (!p.Value.TryGetProperty("type", out var typeElement)) continue;
            var type = typeElement.GetString() ?? ""; types[p.Name] = type;
            if (p.Name == "RouteMeter Local Meter") { hasMeter = type.Equals("Socks5", StringComparison.OrdinalIgnoreCase); continue; }
            if (p.Value.TryGetProperty("all", out _) || new[] { "Direct", "Compatible", "Reject", "RejectDrop", "Pass", "PassRule", "Dns", "Rematch" }.Contains(type, StringComparer.OrdinalIgnoreCase)) continue;
            if (p.Value.TryGetProperty("dialer-proxy", out var dialer) && dialer.GetString() == "RouteMeter Local Meter") metered++;
            else unmetered++;
        }
        return (types, new(hasMeter, metered, unmetered));
    }
    public void Dispose() => http.Dispose();
}
public sealed record ProxyCoverage(bool HasMeter, int Metered, int Unmetered)
{
    public bool Complete => HasMeter && Metered > 0 && Unmetered == 0;
}
