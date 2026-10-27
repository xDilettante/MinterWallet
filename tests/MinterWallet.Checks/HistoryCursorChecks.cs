using System.Net;
using Minter.Protocol;
using MinterWallet.Api;
using MinterWallet.Core;

namespace MinterWallet.Checks;

// History beyond a gate's index: the source's pages first, then the explorer's below the gate's first height, each
// transaction once; the explorer's answer recorded from explorer-api.minter.network (mainnet, 2026-10-27).
internal static class HistoryCursorChecks
{
    private const string Raw = "f8768203490102800da4e3e2e1820ea99474d47b18537bfb005f29bc943c8b1d57749afd868809935f581f050000808001b845f8431ca093f5c0b50fa1915db9013eb3352f2181d13f0ed2ec7d4327e2af38e68e07e6dda03b92273ffe3bddd77e83bcaed352907322936148afd07417c4b5227a2e1f3ad6";
    private const string Sender = "Mxf4a7739c9956dea74ca4ae87be4c9665bb71a310";

    public static async Task Run()
    {
        static TxRecord R(long height) => new($"Mt{height:x64}", height, 0, "", [], new Dictionary<string, string>());

        // Source: a full page (100..71) and a short one (70..66). Explorer: 68..60 with repeats above the gate's start.
        var asked = new List<string>();
        Task<IReadOnlyList<TxRecord>> Source(int page)
        {
            asked.Add($"source {page}");
            return Task.FromResult<IReadOnlyList<TxRecord>>(page switch { 1 => [.. Enumerable.Range(71, 30).Reverse().Select(h => R(h))], 2 => [.. Enumerable.Range(66, 5).Reverse().Select(h => R(h))], _ => [] });
        }
        Task<(IReadOnlyList<TxRecord>, bool)> Older(int page)
        {
            asked.Add($"older {page}");
            return Task.FromResult<(IReadOnlyList<TxRecord>, bool)>(page == 1 ? ([.. Enumerable.Range(64, 5).Reverse().Select(h => R(h))], true) : ([.. Enumerable.Range(60, 4).Reverse().Select(h => R(h))], false));
        }
        var cursor = new HistoryCursor(Source, 30, Older, olderBelow: 66);
        var first = await cursor.NextAsync();
        Assert.Equal(30, first.Count, "first page from the source");
        Assert.True(first.All(x => !x.Older) && first[0].Record.Height == 100 && cursor.HasMore, "newest first, more to come");
        var second = await cursor.NextAsync();
        Assert.Equal("70,69,68,67,66,65,64,63,62,61,60", string.Join(",", second.Select(x => x.Record.Height)), "the source's rest, then the explorer below 66 without repeats");
        Assert.True(second.Where(x => x.Record.Height < 66).All(x => x.Older) && !cursor.HasMore, "older part marked, nothing left");
        Assert.Equal("source 1,source 2,older 1,older 2", string.Join(",", asked), "pages asked in order");

        // Without an explorer the cursor ends with the source.
        var plain = new HistoryCursor(Source, 30);
        await plain.NextAsync(); await plain.NextAsync();
        Assert.True(!plain.HasMore, "source only");

        // The explorer's page: raw bytes decoded by the wallet, more pages announced by its meta.
        using var explorer = new ExplorerClient(new Uri("https://explorer.test/api/v2/"), new Recorded($$$"""
            {"data":[{"txn":6114103,"hash":"Mtb4bee989b6254f3144afd97871c0ec5711e1148021dccfbb5e35131424f2f014","nonce":841,"height":40385997,"timestamp":"2026-10-27T16:07:46Z","type":13,"payload":"","from":"{{{Sender}}}","raw_tx":"{{{Raw}}}"}],
             "links":{},"meta":{"current_page":1,"last_page":2,"per_page":50,"total":51}}
            """));
        var (records, more) = await explorer.HistoryAsync(Address.Parse(Sender), 1);
        Assert.True(more && records.Count == 1 && records[0].Height == 40385997 && records[0].Code == 0, "explorer page");
        var entry = History.Decode(records[0], Address.Parse(Sender), new CoinRegistry().Symbol);
        Assert.True(entry.Summary == "Мультисенд 0.69 #3753 → Mx74d4…fd86" && entry.Hash == records[0].Hash, $"decoded from raw bytes: {entry.Summary}");
    }

    private sealed class Recorded(string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.RequestUri!.PathAndQuery != $"/api/v2/addresses/{Sender}/transactions?page=1") throw new InvalidOperationException($"unexpected request {request.RequestUri}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
