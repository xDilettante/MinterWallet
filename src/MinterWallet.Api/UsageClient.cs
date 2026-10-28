using System.Net;
using System.Text;
using System.Text.Json;
using MinterWallet.Core;

namespace MinterWallet.Api;

public sealed record UsageCount(string Name, long Count);
public sealed record UsageStats(long Online, long Day, long Month, long Total, IReadOnlyList<UsageCount> Versions, IReadOnlyList<UsageCount> Systems);

// Anonymous usage at the project's gate: a ping with a random install id, the version, OS, language and network —
// nothing else: no keys, addresses or amounts — and the counts of everyone's pings. MINTERWALLET_USAGE_URL sends them
// elsewhere, or nowhere with "off" (the checks set it).
public sealed class UsageClient : IDisposable
{
    private readonly HttpClient http;

    public UsageClient(Uri gate, HttpMessageHandler? handler = null)
    {
        handler ??= new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(2), AutomaticDecompression = DecompressionMethods.All };
        http = new HttpClient(handler) { BaseAddress = new Uri(gate.AbsoluteUri.TrimEnd('/') + "/"), Timeout = TimeSpan.FromSeconds(10) };
    }

    public static Uri? Endpoint() => Environment.GetEnvironmentVariable("MINTERWALLET_USAGE_URL") switch
    {
        "off" => null,
        { Length: > 0 } url => new Uri(url),
        _ => new Uri(WalletSettings.DefaultGateUrl),
    };

    public static string Body(string id, string version, string os, string language, string network)
    {
        using var buffer = new MemoryStream();
        using (var json = new Utf8JsonWriter(buffer))
        {
            json.WriteStartObject();
            json.WriteString("id", id); json.WriteString("version", version); json.WriteString("os", os);
            json.WriteString("language", language); json.WriteString("network", network);
            json.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    public static string Os => OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : OperatingSystem.IsMacOS() ? "macos" : "other";

    public async Task PingAsync(string id, string version, string os, string language, string network, CancellationToken ct = default)
    {
        using var reply = await http.PostAsync("gate/v1/ping", new StringContent(Body(id, version, os, language, network), Encoding.UTF8, "application/json"), ct);
        reply.EnsureSuccessStatusCode();
    }

    public async Task<UsageStats> StatsAsync(CancellationToken ct = default)
    {
        using var reply = await http.GetAsync("gate/v1/stats", ct);
        reply.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
        var j = doc.RootElement;
        static IReadOnlyList<UsageCount> Counts(JsonElement list) => list.EnumerateArray().Select(c => new UsageCount(c.GetProperty("name").GetString() ?? "", c.GetProperty("count").GetInt64())).ToList();
        return new UsageStats(j.GetProperty("online").GetInt64(), j.GetProperty("day").GetInt64(), j.GetProperty("month").GetInt64(), j.GetProperty("total").GetInt64(),
            Counts(j.GetProperty("versions")), Counts(j.GetProperty("systems")));
    }

    public void Dispose() => http.Dispose();
}
