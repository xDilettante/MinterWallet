using System.Net;
using System.Text.Json;
using MinterWallet.Api;
using MinterWallet.Core;

namespace MinterWallet.Checks;

// Anonymous usage: a ping carries the five fields and nothing else; the counts are read as the gate gives them.
internal static class UsageChecks
{
    public static async Task Run()
    {
        var endpoint = new Uri("https://gate.test:8851/");
        Assert.True(UsageClient.SkipCertificateCheckFor(endpoint, [new SourceSettings { Url = "https://gate.test:8851", SkipCertificateCheck = true }]), "same endpoint inherits explicit TLS choice");
        Assert.True(!UsageClient.SkipCertificateCheckFor(endpoint, [new SourceSettings { Url = "https://other.test:8851", SkipCertificateCheck = true }]), "unrelated source cannot disable telemetry TLS");
        Assert.True(UsageClient.SkipCertificateCheckFor(endpoint, [new SourceSettings { Url = "https://gate.test:8851/v2", SkipCertificateCheck = true }]), "API path does not change TLS origin");
        Assert.True(!UsageClient.SkipCertificateCheckFor(endpoint, [new SourceSettings { Url = "https://gate.test:8850", SkipCertificateCheck = true }]), "different port cannot disable telemetry TLS");
        Assert.True(!UsageClient.SkipCertificateCheckFor(endpoint, [new SourceSettings { Url = "https://gate.test:8851", SkipCertificateCheck = false }]), "strict remains default");
        var handler = new Recorder();
        using var usage = new UsageClient(new Uri("https://gate.test:8851"), handler);
        await usage.PingAsync("0123456789abcdef0123456789abcdef", "0.4.0", "windows", "ru", "mainnet");
        Assert.True(handler.Request is { Method.Method: "POST" } r && r.RequestUri!.AbsoluteUri == "https://gate.test:8851/gate/v1/ping", "the ping goes to the gate");
        using (var body = JsonDocument.Parse(handler.Body!))
            Assert.Equal("id,version,os,language,network", string.Join(",", body.RootElement.EnumerateObject().Select(p => p.Name)), "five fields, nothing else");
        var stats = await usage.StatsAsync();
        Assert.True(stats is { Online: 3, Day: 10, Month: 40, Total: 120 } && stats.Versions is [{ Name: "0.4.0", Count: 30 }, ..] && stats.Systems.Count == 2, "the counts read");
        await usage.PingAsync("0123456789abcdef0123456789abcdef", "0.8.5", "android", "ru", "mainnet");
        Assert.True(handler.Posts == 2 && handler.Body!.Contains("\"os\":\"android\"", StringComparison.Ordinal), "modern gate receives Android unchanged in one request");
        var legacy = new LegacyGate();
        using (var android = new UsageClient(endpoint, legacy))
            await android.PingAsync("0123456789abcdef0123456789abcdef", "0.8.5", "android", "ru", "mainnet");
        Assert.True(legacy.Bodies.Count == 2, "Android explicitly rejected by an old gate uses its supported other category");
        using (var first = JsonDocument.Parse(legacy.Bodies[0]))
        using (var second = JsonDocument.Parse(legacy.Bodies[1]))
        {
            Assert.Equal("android", first.RootElement.GetProperty("os").GetString()!, "first attempt identifies Android correctly");
            Assert.Equal("other", second.RootElement.GetProperty("os").GetString()!, "compatibility category understood by old gates");
            foreach (var name in new[] { "id", "version", "language", "network" })
                Assert.Equal(first.RootElement.GetProperty(name).GetString()!, second.RootElement.GetProperty(name).GetString()!, "compatibility preserves " + name);
        }
        foreach (var rejection in new[] { "network: mainnet, test or unknown", "<html>Bad Request</html>" })
        {
            var refusing = new LegacyGate { Rejection = rejection };
            using var client = new UsageClient(endpoint, refusing);
            try { await client.PingAsync(new string('a', 32), "0.8.5", "android", "ru", "mainnet"); throw new Exception("gate rejection must remain visible"); }
            catch (HttpRequestException e) { Assert.True(e.StatusCode == HttpStatusCode.BadRequest, "original rejection is preserved"); }
            Assert.True(refusing.Bodies.Count == 1, "unrelated or unrecognized POST errors are never replayed");
        }
        Environment.SetEnvironmentVariable("MINTERWALLET_USAGE_URL", "off");
        Assert.True(UsageClient.Endpoint() == null, "off turns the pings off");
        Environment.SetEnvironmentVariable("MINTERWALLET_USAGE_URL", null);
        Assert.Equal("https://188.127.237.145:8851/", UsageClient.Endpoint()!.AbsoluteUri, "the project's gate by default");
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        public int Posts;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) { Posts++; Request = request; Body = await request.Content!.ReadAsStringAsync(ct); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"online":3,"day":10,"month":40,"total":120,"versions":[{"name":"0.4.0","count":30},{"name":"0.3.0","count":10}],"systems":[{"name":"windows","count":35},{"name":"linux","count":5}]}
                """) };
        }
    }

    private sealed class LegacyGate : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];
        public string Rejection { get; init; } = "os: windows, linux, macos or other";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return Bodies.Count == 1
                ? new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent(JsonSerializer.Serialize(new { error = new { code = "400", message = Rejection } })) }
                : new HttpResponseMessage(HttpStatusCode.NoContent);
        }
    }
}
