using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Minter.Protocol;

namespace MinterWallet.Core;

// A coin that came to the account (+) or left it (−) with a transaction.
public sealed record TxMove(BigInteger Amount, uint Coin);

// A transaction as the history shows it: what it was (Title), with whom or along which route (Subtitle), what it moved on
// this account, and every detail for the full view.
public sealed record TxDetails(string Title, string Subtitle, IReadOnlyList<TxMove> Moves, string Commission, IReadOnlyList<(string Label, string Value)> Rows, Address? Counterparty = null);

// Read from the transaction's own bytes; what only the execution knows — what a swap brought (tx.return), what a sale of
// everything sold (tx.sell_amount), the volumes of liquidity, the route through the pools (tx.pools), the commission —
// from the source's tags. A transaction from the explorer comes without them, and its swap shows only what it gave.
public static class TxExplain
{
    public static TxDetails Explain(TxRecord record, Address me, Func<uint, string> symbol)
    {
        var tx = Transaction.Decode(record.Raw);
        var from = tx.Sender();
        ITxData? data = null;
        try { data = tx.DecodeData(); } catch (FormatException) { }
        var tags = record.Tags;
        var ok = record.Code == 0;
        var mine = from == me;
        BigInteger? Tag(string name) => tags.TryGetValue(name, out var v) && BigInteger.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
        string A(BigInteger v, uint coin) => $"{Units.FormatCoins(v)} {symbol(coin)}";
        string Route(IEnumerable<uint> coins) => string.Join(" → ", coins.Select(symbol));

        var moves = new List<TxMove>();
        void Out(BigInteger? v, uint coin) { if (ok && v is { Sign: > 0 } x) moves.Add(new TxMove(-x, coin)); }
        void In(BigInteger? v, uint coin) { if (ok && v is { Sign: > 0 } x) moves.Add(new TxMove(x, coin)); }

        var rows = new List<(string, string)>
        {
            ("Статус", ok ? "выполнена" : $"не выполнена (код {record.Code}): {record.Log}"),
            ("Блок", record.Height.ToString("N0", CultureInfo.InvariantCulture).Replace(',', ' ')),
            ("Хеш", record.Hash),
            ("Тип", $"{tx.Type} (0x{(byte)tx.Type:X2})"),
            ("Отправитель", from.ToString()),
        };
        string title, subtitle = "";
        Address? counterparty = null;
        switch (data)
        {
            case SendData s when mine:
                title = "Перевод"; subtitle = $"→ {History.Short(s.To)}"; counterparty = s.To; Out(s.Value, s.Coin);
                if (s.To == me) In(s.Value, s.Coin);
                rows.Add(("Получатель", s.To.ToString())); rows.Add(("Сумма", A(s.Value, s.Coin)));
                break;
            case SendData s:
                title = "Получено"; subtitle = $"от {History.Short(from)}"; counterparty = from; In(s.Value, s.Coin);
                rows.Add(("Получатель", s.To.ToString())); rows.Add(("Сумма", A(s.Value, s.Coin)));
                break;
            case MultisendData m when mine:
                title = "Мультисенд"; subtitle = $"получателей: {m.List.Count}";
                foreach (var g in m.List.GroupBy(i => i.Coin)) Out(g.Aggregate(BigInteger.Zero, (t, i) => t + i.Value), g.Key);
                foreach (var i in m.List.Where(i => i.To == me)) In(i.Value, i.Coin);
                rows.Add(("Получатели", string.Join("\n", m.List.Select(i => $"{i.To} — {A(i.Value, i.Coin)}"))));
                break;
            case MultisendData m:
                title = "Получено"; subtitle = $"от {History.Short(from)} (мультисенд)"; counterparty = from;
                foreach (var i in m.List.Where(i => i.To == me)) In(i.Value, i.Coin);
                rows.Add(("Получатели", string.Join("\n", m.List.Select(i => $"{i.To} — {A(i.Value, i.Coin)}"))));
                break;
            case SellCoinData s:
                title = "Обмен"; subtitle = $"{symbol(s.CoinToSell)} → {symbol(s.CoinToBuy)} (резерв)";
                Out(s.ValueToSell, s.CoinToSell); In(Tag("tx.return"), s.CoinToBuy);
                rows.Add(("Гарантия", $"получить не меньше {A(s.MinimumValueToBuy, s.CoinToBuy)}"));
                break;
            case SellAllCoinData s:
                title = "Обмен всего"; subtitle = $"{symbol(s.CoinToSell)} → {symbol(s.CoinToBuy)} (резерв)";
                Out(Tag("tx.sell_amount"), s.CoinToSell); In(Tag("tx.return"), s.CoinToBuy);
                rows.Add(("Гарантия", $"получить не меньше {A(s.MinimumValueToBuy, s.CoinToBuy)}"));
                break;
            case BuyCoinData b:
                title = "Покупка"; subtitle = $"{symbol(b.CoinToSell)} → {symbol(b.CoinToBuy)} (резерв)";
                Out(Tag("tx.return"), b.CoinToSell); In(b.ValueToBuy, b.CoinToBuy);
                rows.Add(("Гарантия", $"заплатить не больше {A(b.MaximumValueToSell, b.CoinToSell)}"));
                break;
            case SellSwapPoolData s:
                title = "Обмен"; subtitle = Route(s.Coins);
                Out(s.ValueToSell, s.Coins[0]); In(Tag("tx.return"), s.Coins[^1]);
                rows.Add(("Гарантия", $"получить не меньше {A(s.MinimumValueToBuy, s.Coins[^1])}"));
                break;
            case SellAllSwapPoolData s:
                title = "Обмен всего"; subtitle = Route(s.Coins);
                Out(Tag("tx.sell_amount"), s.Coins[0]); In(Tag("tx.return"), s.Coins[^1]);
                rows.Add(("Гарантия", $"получить не меньше {A(s.MinimumValueToBuy, s.Coins[^1])}"));
                break;
            case BuySwapPoolData b:
                title = "Покупка"; subtitle = Route(b.Coins);
                Out(Tag("tx.return"), b.Coins[0]); In(b.ValueToBuy, b.Coins[^1]);
                rows.Add(("Гарантия", $"заплатить не больше {A(b.MaximumValueToSell, b.Coins[0])}"));
                break;
            case DelegateData d:
                title = "Делегирование"; subtitle = $"валидатору {History.Short(d.PubKey)}"; Out(d.Value, d.Coin);
                rows.Add(("Валидатор", d.PubKey.ToString()));
                break;
            case UnbondData u:
                title = "Вывод из стейка"; subtitle = $"{A(u.Value, u.Coin)} у {History.Short(u.PubKey)}";
                rows.Add(("Валидатор", u.PubKey.ToString())); rows.Add(("Сумма", $"{A(u.Value, u.Coin)} — вернётся на баланс после срока вывода"));
                break;
            case MoveStakeData m:
                title = "Перенос стейка"; subtitle = $"{History.Short(m.FromPubKey)} → {History.Short(m.ToPubKey)}";
                rows.Add(("Сумма", A(m.Value, m.Coin))); rows.Add(("Откуда", m.FromPubKey.ToString())); rows.Add(("Куда", m.ToPubKey.ToString()));
                break;
            case RedeemCheckData r:
                Check? check = null;
                try { check = Check.Decode(r.RawCheck); } catch (FormatException) { }
                title = mine ? "Погашение чека" : "Ваш чек погашен"; subtitle = mine ? "" : $"получатель {History.Short(from)}";
                if (check != null) { if (mine) In(check.Value, check.Coin); else Out(check.Value, check.Coin); rows.Add(("Чек", A(check.Value, check.Coin))); }
                break;
            case AddLiquidityData l:
                title = "Добавление ликвидности"; subtitle = $"пул {symbol(l.Coin0)}/{symbol(l.Coin1)}";
                Out(l.Volume0, l.Coin0); Out(Tag("tx.volume1"), l.Coin1);
                if (Tag("tx.pool_token_id") is { } lpIn && Tag("tx.liquidity") is { } got) In(got, (uint)lpIn);
                break;
            case RemoveLiquidityData l:
                title = "Вывод ликвидности"; subtitle = $"пул {symbol(l.Coin0)}/{symbol(l.Coin1)}";
                if (Tag("tx.pool_token_id") is { } lpOut) Out(l.Liquidity, (uint)lpOut);
                In(Tag("tx.volume0"), l.Coin0); In(Tag("tx.volume1"), l.Coin1);
                break;
            case MintTokenData t: title = "Выпуск"; subtitle = symbol(t.Coin); In(t.Value, t.Coin); break;
            case BurnTokenData t: title = "Сжигание"; subtitle = symbol(t.Coin); Out(t.Value, t.Coin); break;
            case LockData l: title = "Блокировка"; subtitle = $"до блока {l.DueBlock}"; Out(l.Value, l.Coin); break;
            case AddLimitOrderData o:
                title = "Лимит-ордер"; subtitle = $"продать за {A(o.ValueToBuy, o.CoinToBuy)}"; Out(o.ValueToSell, o.CoinToSell);
                break;
            default:
                title = data == null ? $"Тип 0x{(byte)tx.Type:X2}" : History.Describe(data, from, me, symbol);
                break;
        }

        // The route through the pools with the amounts at each step.
        if (tags.TryGetValue("tx.pools", out var pools) && Hops(pools, symbol) is { Length: > 0 } hops) rows.Add(("Маршрут", hops));
        var commission = "";
        if (mine && Tag("tx.commission_amount") is { } fee && tags.TryGetValue("tx.commission_coin", out var feeCoin) && uint.TryParse(feeCoin, NumberStyles.None, CultureInfo.InvariantCulture, out var feeId))
        {
            commission = A(fee, feeId);
            rows.Add(("Комиссия", commission + (tags.TryGetValue("tx.commission_conversion", out var conv) && conv == "pool" ? " (обменяна в пуле на базовую монету)" : "")));
        }
        rows.Add(("Gas price", tx.GasPrice.ToString(CultureInfo.InvariantCulture)));
        rows.Add(("Nonce", tx.Nonce.ToString(CultureInfo.InvariantCulture)));
        if (tx.Payload.Length > 0) rows.Add(("Сообщение", History.PayloadText(tx.Payload)));
        return new TxDetails(title, subtitle, moves, commission, rows, counterparty);
    }

    // tx.pools: [{"pool_id", "coin_in", "value_in", "coin_out", "value_out"}, …] — a line per pool.
    private static string Hops(string json, Func<uint, string> symbol)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return "";
            return string.Join("\n", doc.RootElement.EnumerateArray().Select(p =>
                $"{Units.FormatCoins(Big(p, "value_in"))} {symbol(Id(p, "coin_in"))} → {Units.FormatCoins(Big(p, "value_out"))} {symbol(Id(p, "coin_out"))} (пул #{Id(p, "pool_id")})"));
        }
        catch (Exception e) when (e is JsonException or KeyNotFoundException or InvalidOperationException or FormatException) { return ""; }

        static BigInteger Big(JsonElement e, string name) => BigInteger.Parse(e.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
        static uint Id(JsonElement e, string name) => e.GetProperty(name).ValueKind == JsonValueKind.Number ? e.GetProperty(name).GetUInt32() : uint.Parse(e.GetProperty(name).GetString()!, CultureInfo.InvariantCulture);
    }
}
