using System.Net;
using System.Text.Json;
using MinterWallet.Api;

namespace MinterWallet.Checks;

// Anonymous usage: a ping carries the five fields and nothing else; the counts are read as the gate gives them.
internal static class UsageChecks
{
    public static async Task Run()
    {
        var handler = new Recorder();
        using var usage = new UsageClient(new Uri("https://gate.test:8851"), handler);
        await usage.PingAsync("0123456789abcdef0123456789abcdef", "0.4.0", "windows", "ru", "mainnet");
        Assert.True(handler.Request is { Method.Method: "POST" } r && r.RequestUri!.AbsoluteUri == "https://gate.test:8851/gate/v1/ping", "the ping goes to the gate");
        using (var body = JsonDocument.Parse(handler.Body!))
            Assert.Equal("id,version,os,language,network", string.Join(",", body.RootElement.EnumerateObject().Select(p => p.Name)), "five fields, nothing else");
        var stats = await usage.StatsAsync();
        Assert.True(stats is { Online: 3, Day: 10, Month: 40, Total: 120 } && stats.Versions is [{ Name: "0.4.0", Count: 30 }, ..] && stats.Systems.Count == 2, "the counts read");
        Environment.SetEnvironmentVariable("MINTERWALLET_USAGE_URL", "off");
        Assert.True(UsageClient.Endpoint() == null, "off turns the pings off");
        Environment.SetEnvironmentVariable("MINTERWALLET_USAGE_URL", null);
        Assert.Equal("https://188.127.237.145:8851/", UsageClient.Endpoint()!.AbsoluteUri, "the project's gate by default");
    }

    private sealed class Recorder : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Post) { Request = request; Body = await request.Content!.ReadAsStringAsync(ct); return new HttpResponseMessage(HttpStatusCode.NoContent); }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("""
                {"online":3,"day":10,"month":40,"total":120,"versions":[{"name":"0.4.0","count":30},{"name":"0.3.0","count":10}],"systems":[{"name":"windows","count":35},{"name":"linux","count":5}]}
                """) };
        }
    }
}
