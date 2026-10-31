using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using MinterWallet.Api;
using MinterWallet.Core;

namespace MinterWallet.Checks;

internal static class RateLimitChecks
{
    private const string Status = """{"network":"minter-devnet","version":"test","latest_block_height":"1","latest_block_time":"2026-10-29T00:00:00Z"}""";
    public static async Task Run()
    {
        var limited = new Limited(1);
        using (var api = new NodeApiClient(new Uri("https://retry.test"), handler: limited))
        {
            Assert.Equal(1L, (await api.StatusAsync()).Height, "startup GET recovers from nginx HTML 429");
            Assert.Equal(2, limited.Calls, "one bounded retry");
        }
        var forever = new Limited(100);
        using (var api = new NodeApiClient(new Uri("https://limit.test"), handler: forever))
        {
            try { await api.StatusAsync(); throw new Exception("expected exhausted 429"); }
            catch (ChainApiException e)
            {
                Assert.Equal(429, e.Code, "429 kept as HTTP error");
                Assert.True(!e.Message.Contains("JSON", StringComparison.Ordinal), "HTML rate limit is not a JSON parsing error");
                Assert.Equal(3, forever.Calls, "retry limit prevents storm");
            }
            await Assert.ThrowsAsync<ChainApiException>(async () => { await api.GateAsync(); }, "429 does not downgrade a gate to a node without history");
        }
        var write = new Limited(100);
        using (var http = new HttpClient(new SourceRequestHandler(write, TimeSpan.Zero)))
        {
            using var reply = await http.PostAsync("https://write-once.test/send_transaction", new StringContent("signed transaction"));
            Assert.Equal(HttpStatusCode.TooManyRequests, reply.StatusCode, "write refusal returned");
            Assert.Equal(1, write.Calls, "POST is never automatically replayed");
        }
        var probe = new Probe();
        var origin = "https://shared-" + Guid.NewGuid().ToString("N") + ".test/";
        using (var a = new HttpClient(new SourceRequestHandler(new Measured(probe), TimeSpan.FromMilliseconds(30))))
        using (var b = new HttpClient(new SourceRequestHandler(new Measured(probe), TimeSpan.FromMilliseconds(30))))
        {
            var replies = await Task.WhenAll(Enumerable.Range(0, 12).Select(i => (i % 2 == 0 ? a : b).GetAsync(origin + i)));
            foreach (var reply in replies) reply.Dispose();
            Assert.True(probe.Maximum <= 4, "shared concurrency cap includes slow response bodies");
            Assert.True(probe.Starts[^1] - probe.Starts[0] >= Stopwatch.Frequency * .30, "shared pacing across client instances");
        }

        // Foreground UI requests keep a reserved concurrency slot and jump ahead of queued background scan/watch work.
        var hold = new Held();
        var priorityOrigin = "https://priority-" + Guid.NewGuid().ToString("N") + ".test/";
        using (var http = new HttpClient(new SourceRequestHandler(hold, TimeSpan.Zero)))
        {
            Task<HttpResponseMessage>[] bg;
            using (SourceRequestHandler.EnterBackground())
                bg = [.. Enumerable.Range(0, 5).Select(i => http.GetAsync($"{priorityOrigin}bg-{i}"))];
            await hold.WaitForCountAsync(3);
            var fg = await http.GetAsync($"{priorityOrigin}fg");
            fg.Dispose();
            Assert.Equal("bg-0,bg-1,bg-2,fg", string.Join(",", hold.Order), "foreground request uses reserved slot while background requests queue");
            hold.ReleaseAll();
            foreach (var r in await Task.WhenAll(bg)) r.Dispose();
        }

        using var cancel = new CancellationTokenSource(30);
        using var waiting = new HttpClient(new SourceRequestHandler(new Limited(100), TimeSpan.Zero));
        await Assert.ThrowsAsync<OperationCanceledException>(() => waiting.GetAsync("https://cancel-limit.test/", cancel.Token), "rate-limit wait cancellable");
    }
    private sealed class Held : HttpMessageHandler
    {
        private readonly TaskCompletionSource release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Order { get; } = [];
        public void ReleaseAll() => release.TrySetResult();
        public async Task WaitForCountAsync(int n)
        {
            for (var i = 0; i < 100; i++) { lock (Order) if (Order.Count >= n) return; await Task.Delay(10); }
            throw new TimeoutException($"expected {n} requests, saw {Order.Count}");
        }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var tag = request.RequestUri!.AbsolutePath.TrimStart('/');
            lock (Order) Order.Add(tag);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = tag.StartsWith("bg-", StringComparison.Ordinal) ? new HeldBody(release.Task) : new StringContent("{}")
            });
        }
    }
    private sealed class HeldBody(Task gate) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 2; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await gate;
            await stream.WriteAsync("{}"u8.ToArray());
        }
    }
    private sealed class Probe
    {
        public int Active, Maximum;
        public List<long> Starts { get; } = [];
        public void Begin() { lock (this) { Maximum = Math.Max(Maximum, ++Active); Starts.Add(Stopwatch.GetTimestamp()); } }
        public void End() { lock (this) Active--; }
    }
    private sealed class Measured(Probe probe) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            probe.Begin();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new SlowBody(probe) });
        }
    }
    private sealed class SlowBody(Probe probe) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 2; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await Task.Delay(180);
            await stream.WriteAsync("{}"u8.ToArray());
            probe.End();
        }
    }
    internal sealed class Limited(int refusals) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var limited = ++Calls <= refusals;
            var response = new HttpResponseMessage(limited ? HttpStatusCode.TooManyRequests : HttpStatusCode.OK)
            { Content = new StringContent(limited ? "<html>429 Too Many Requests</html>" : Status) };
            if (limited) response.Headers.RetryAfter = new RetryConditionHeaderValue(TimeSpan.Zero);
            return Task.FromResult(response);
        }
    }
}
