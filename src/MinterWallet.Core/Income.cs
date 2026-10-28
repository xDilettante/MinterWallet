using System.Numerics;
using Minter.Protocol;

namespace MinterWallet.Core;

public enum ArrivalKind { Transfer, Bridge, Reward, Other }

// Coins that came to one of the wallet's addresses since the previous look: how much of which coin, and what brought them.
public sealed record Arrival(WalletAccount Account, CoinRef Coin, BigInteger Amount, ArrivalKind Kind, Address? From);

// Arrivals on the wallet's addresses, seen by comparing each address's balances with the previous look (the first look
// at an address only remembers it). What brought a coin is told by the address's incoming transactions since then: a
// send or a multisend names its sender, the bridge's multisig a deposit from Ethereum or BSC. Growth with none of them is
// a staking reward when the address delegates and the coin is the base one, otherwise a plain credit (an unbond coming
// back, a lock ending) — unless the address sent a transaction of its own meanwhile: then it is a swap, a sale or
// liquidity taken out, the user's own doing, and not reported. One watch per source: another source starts afresh.
public sealed class IncomeWatch(IChainBackend backend)
{
    private sealed record Look(ulong Nonce, Dictionary<uint, BigInteger> Coins, long Height);
    private readonly Dictionary<Address, Look> looks = [];
    private long seen = -1;

    public IChainBackend Backend => backend;

    public async Task<IReadOnlyList<Arrival>> CheckAsync(IReadOnlyList<WalletAccount> accounts, CancellationToken ct = default)
    {
        var height = (await backend.StatusAsync(ct)).Height;
        var result = new List<Arrival>();
        // Balances change only with a block: the same height again (checks every few seconds) asks nothing more.
        if (height == seen && accounts.All(a => looks.ContainsKey(a.Address))) return result;
        seen = height;
        foreach (var account in accounts.DistinctBy(a => a.Address))
        {
            var state = await backend.AccountAsync(account.Address, ct);
            var coins = state.Balances.GroupBy(b => b.Coin.Id).ToDictionary(g => g.Key, g => g.Aggregate(BigInteger.Zero, (s, b) => s + b.Value));
            var had = looks.GetValueOrDefault(account.Address);
            looks[account.Address] = new Look(state.Nonce, coins, height);
            if (had == null) continue;
            var grown = coins.Where(c => c.Value > had.Coins.GetValueOrDefault(c.Key)).ToList();
            if (grown.Count == 0) continue;
            var incoming = await IncomingAsync(account.Address, had.Height, ct);
            foreach (var (coin, value) in grown)
            {
                var amount = value - had.Coins.GetValueOrDefault(coin);
                var symbol = state.Balances.First(b => b.Coin.Id == coin).Coin;
                if (incoming.FirstOrDefault(i => i.Coin == coin).Sender is { } sender)
                    result.Add(new Arrival(account, symbol, amount, sender == Bridge.Multisig ? ArrivalKind.Bridge : ArrivalKind.Transfer, sender));
                else if (state.Nonce == had.Nonce)
                    result.Add(new Arrival(account, symbol, amount, coin == 0 && state.Delegated.Count > 0 ? ArrivalKind.Reward : ArrivalKind.Other, null));
            }
        }
        return result;
    }

    // Who sent the address which coins since a height, newest first: sends to it and its lines of multisends. A source
    // without a transaction index gives nothing here, and arrivals are then told by the balances alone.
    private async Task<List<(uint Coin, Address? Sender)>> IncomingAsync(Address me, long since, CancellationToken ct)
    {
        var result = new List<(uint, Address?)>();
        IReadOnlyList<TxRecord> records;
        try { records = await backend.HistoryAsync(me, 1, 20, ct); }
        catch (Exception e) when (!ct.IsCancellationRequested && e is ChainApiException or HttpRequestException or TaskCanceledException) { return result; }
        foreach (var record in records.Where(r => r.Height > since && r.Code == 0).OrderByDescending(r => r.Height))
        {
            Address sender;
            ITxData data;
            try { var tx = Transaction.Decode(record.Raw); sender = tx.Sender(); data = tx.DecodeData(); }
            catch (FormatException) { continue; }
            if (sender == me) continue;
            IEnumerable<uint> coins = data switch
            {
                SendData s when s.To == me => [s.Coin],
                MultisendData m => m.List.Where(i => i.To == me).Select(i => i.Coin),
                _ => [],
            };
            foreach (var coin in coins) result.Add((coin, sender));
        }
        return result;
    }
}
