using System.Net;
using MinterWallet.Api;

namespace MinterWallet.Checks;

// The update notice: GitHub's latest release (answer shaped as api.github.com returns it) against the running version.
internal static class UpdateChecks
{
    public static async Task Run()
    {
        using var updates = new UpdateClient(new Recorded("""
            {"html_url":"https://github.com/xDilettante/MinterWallet/releases/tag/v0.2.0","tag_name":"v0.2.0","name":"Minter Wallet 0.2.0","draft":false,"prerelease":false}
            """));
        var latest = await updates.LatestAsync();
        Assert.True(latest is { Name: "Minter Wallet 0.2.0" } && latest.Version == new Version(0, 2, 0) && latest.Page.AbsoluteUri.EndsWith("/v0.2.0", StringComparison.Ordinal), "latest release read");
        Assert.True(UpdateClient.IsNewer(new Version(0, 2, 0), new Version(0, 1, 0)), "0.2.0 is newer than 0.1.0");
        Assert.True(!UpdateClient.IsNewer(new Version(0, 1, 0), new Version(0, 1, 0, 0)), "the same version is not offered");
        Assert.True(!UpdateClient.IsNewer(new Version(0, 1), new Version(0, 1, 0)), "0.1 and 0.1.0 are the same");
    }

    private sealed class Recorded(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri != UpdateClient.Latest) throw new InvalidOperationException($"unexpected request {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
