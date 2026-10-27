using System.Net;
using System.Text.Json;

namespace MinterWallet.Api;

// New versions of the wallet: the latest release of the public repository (GitHub's API, no account; GitHub sees the
// request like any page visit). Pre-releases are not offered.
public sealed class UpdateClient : IDisposable
{
    public static readonly Uri Latest = new("https://api.github.com/repos/xDilettante/MinterWallet/releases/latest");
    public sealed record Release(Version Version, string Name, Uri Page);
    private readonly HttpClient http;

    public UpdateClient(HttpMessageHandler? handler = null)
    {
        http = new HttpClient(handler ?? new SocketsHttpHandler { AutomaticDecompression = DecompressionMethods.All }) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("MinterWallet");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<Release?> LatestAsync(CancellationToken ct = default)
    {
        using var reply = await http.GetAsync(Latest, ct);
        if (!reply.IsSuccessStatusCode) return null;
        using var doc = JsonDocument.Parse(await reply.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version)) return null;
        return new Release(version, root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag, new Uri(root.GetProperty("html_url").GetString()!));
    }

    // Newer than the running version (both as major.minor.build).
    public static bool IsNewer(Version latest, Version current) => new Version(latest.Major, latest.Minor, Math.Max(0, latest.Build)) > new Version(current.Major, current.Minor, Math.Max(0, current.Build));

    public void Dispose() => http.Dispose();
}
