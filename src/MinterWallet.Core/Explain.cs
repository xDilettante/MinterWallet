using System.Globalization;
using System.Numerics;
using System.Text.Json;
using Minter.Protocol;

namespace MinterWallet.Core;

// A coin that came to the account (+) or left it (−) with a transaction.
public sealed record TxMove(BigInteger Amount, uint Coin, string? Note = null);

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
        void Out(BigInteger? v, uint coin, string? note = null) { if (ok && v is { Sign: > 0 } x) moves.Add(new TxMove(-x, coin, note)); }
        void In(BigInteger? v, uint coin, string? note = null) { if (ok && v is { Sign: > 0 } x) moves.Add(new TxMove(x, coin, note)); }

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
                title = $"Обмен {symbol(s.CoinToSell)} → {symbol(s.CoinToBuy)}";
                subtitle = $"{symbol(s.CoinToSell)} → {symbol(s.CoinToBuy)} (резерв)";
                Out(s.ValueToSell, s.CoinToSell);
                rows.Add(("Отдано", A(s.ValueToSell, s.CoinToSell)));
                if (Tag("tx.return") is { } sRet)
                {
                    In(sRet, s.CoinToBuy);
                    rows.Add(("Получено", A(sRet, s.CoinToBuy)));
                    if (Rate(s.ValueToSell, s.CoinToSell, sRet, s.CoinToBuy, symbol) is { Length: > 0 } sRate) rows.Add(("Курс", sRate));
                }
                else
                {
                    In(s.MinimumValueToBuy, s.CoinToBuy, "≥");
                    rows.Add(("Получено", $"{A(s.MinimumValueToBuy, s.CoinToBuy)} (минимум по гарантии)"));
                }
                rows.Add(("Гарантия", $"получить не меньше {A(s.MinimumValueToBuy, s.CoinToBuy)}"));
                break;
            case SellSwapPoolData s:
                title = $"Обмен {symbol(s.Coins[0])} → {symbol(s.Coins[^1])}";
                subtitle = s.Coins.Count > 2 ? Route(s.Coins) : $"пул {symbol(s.Coins[0])}/{symbol(s.Coins[1])}";
                Out(s.ValueToSell, s.Coins[0]);
                rows.Add(("Отдано", A(s.ValueToSell, s.Coins[0])));
                if (Tag("tx.return") is { } spRet)
                {
                    In(spRet, s.Coins[^1]);
                    rows.Add(("Получено", A(spRet, s.Coins[^1])));
                    if (Rate(s.ValueToSell, s.Coins[0], spRet, s.Coins[^1], symbol) is { Length: > 0 } spRate) rows.Add(("Курс", spRate));
                }
                else
                {
                    In(s.MinimumValueToBuy, s.Coins[^1], "≥");
                    rows.Add(("Получено", $"{A(s.MinimumValueToBuy, s.Coins[^1])} (минимум по гарантии)"));
                }
                rows.Add(("Гарантия", $"получить не меньше {A(s.MinimumValueToBuy, s.Coins[^1])}"));
                break;
            case SellAllCoinData s:
                title = $"Обмен всего {symbol(s.CoinToSell)} → {symbol(s.CoinToBuy)}";
                subtitle = $"{symbol(s.CoinToSell)} → {symbol(s.CoinToBuy)} (резерв)";
                if (Tag("tx.sell_amount") is { } saSold) { Out(saSold, s.CoinToSell); rows.Add(("Отдано", A(saSold, s.CoinToSell))); }
                else { moves.Add(new TxMove(BigInteger.Zero, s.CoinToSell, "все")); rows.Add(("Отдано", $"все {symbol(s.CoinToSell)}")); }
                if (Tag("tx.return") is { } saRet)
                {
                    In(saRet, s.CoinToBuy);
                    rows.Add(("Получено", A(saRet, s.CoinToBuy)));
                    if (Tag("tx.sell_amount") is { } saAmt && Rate(saAmt, s.CoinToSell, saRet, s.CoinToBuy, symbol) is { Length: > 0 } saRate) rows.Add(("Курс", saRate));
                }
                else
                {
                    In(s.MinimumValueToBuy, s.CoinToBuy, "≥");
                    rows.Add(("Получено", $"{A(s.MinimumValueToBuy, s.CoinToBuy)} (минимум по гарантии)"));
                }
                rows.Add(("Гарантия", $"получить не меньше {A(s.MinimumValueToBuy, s.CoinToBuy)}"));
                break;
            case SellAllSwapPoolData s:
                title = $"Обмен всего {symbol(s.Coins[0])} → {symbol(s.Coins[^1])}";
                subtitle = s.Coins.Count > 2 ? Route(s.Coins) : $"пул {symbol(s.Coins[0])}/{symbol(s.Coins[1])}";
                if (Tag("tx.sell_amount") is { } sapSold) { Out(sapSold, s.Coins[0]); rows.Add(("Отдано", A(sapSold, s.Coins[0]))); }
                else { moves.Add(new TxMove(BigInteger.Zero, s.Coins[0], "все")); rows.Add(("Отдано", $"все {symbol(s.Coins[0])}")); }
                if (Tag("tx.return") is { } sapRet)
                {
                    In(sapRet, s.Coins[^1]);
                    rows.Add(("Получено", A(sapRet, s.Coins[^1])));
                    if (Tag("tx.sell_amount") is { } sapAmt && Rate(sapAmt, s.Coins[0], sapRet, s.Coins[^1], symbol) is { Length: > 0 } sapRate) rows.Add(("Курс", sapRate));
                }
                else
                {
                    In(s.MinimumValueToBuy, s.Coins[^1], "≥");
                    rows.Add(("Получено", $"{A(s.MinimumValueToBuy, s.Coins[^1])} (минимум по гарантии)"));
                }
                rows.Add(("Гарантия", $"получить не меньше {A(s.MinimumValueToBuy, s.Coins[^1])}"));
                break;
            case BuyCoinData b:
                title = $"Покупка {symbol(b.CoinToBuy)} за {symbol(b.CoinToSell)}";
                subtitle = $"{symbol(b.CoinToSell)} → {symbol(b.CoinToBuy)} (резерв)";
                if (Tag("tx.return") is { } bPaid) { Out(bPaid, b.CoinToSell); rows.Add(("Оплачено", A(bPaid, b.CoinToSell))); }
                else { Out(b.MaximumValueToSell, b.CoinToSell, "≤"); rows.Add(("Оплачено", $"{A(b.MaximumValueToSell, b.CoinToSell)} (максимум по гарантии)")); }
                In(b.ValueToBuy, b.CoinToBuy);
                rows.Add(("Куплено", A(b.ValueToBuy, b.CoinToBuy)));
                if (Tag("tx.return") is { } bPaidRet && Rate(bPaidRet, b.CoinToSell, b.ValueToBuy, b.CoinToBuy, symbol) is { Length: > 0 } bRate) rows.Add(("Курс", bRate));
                rows.Add(("Гарантия", $"заплатить не больше {A(b.MaximumValueToSell, b.CoinToSell)}"));
                break;
            case BuySwapPoolData b:
                title = $"Покупка {symbol(b.Coins[^1])} за {symbol(b.Coins[0])}";
                subtitle = b.Coins.Count > 2 ? Route(b.Coins) : $"пул {symbol(b.Coins[0])}/{symbol(b.Coins[1])}";
                if (Tag("tx.return") is { } bpPaid) { Out(bpPaid, b.Coins[0]); rows.Add(("Оплачено", A(bpPaid, b.Coins[0]))); }
                else { Out(b.MaximumValueToSell, b.Coins[0], "≤"); rows.Add(("Оплачено", $"{A(b.MaximumValueToSell, b.Coins[0])} (максимум по гарантии)")); }
                In(b.ValueToBuy, b.Coins[^1]);
                rows.Add(("Куплено", A(b.ValueToBuy, b.Coins[^1])));
                if (Tag("tx.return") is { } bpPaidRet && Rate(bpPaidRet, b.Coins[0], b.ValueToBuy, b.Coins[^1], symbol) is { Length: > 0 } bpRate) rows.Add(("Курс", bpRate));
                rows.Add(("Гарантия", $"заплатить не больше {A(b.MaximumValueToSell, b.Coins[0])}"));
                break;
            case DelegateData d:
                title = $"Делегирование {symbol(d.Coin)}"; subtitle = $"валидатору {History.Short(d.PubKey)}"; Out(d.Value, d.Coin);
                rows.Add(("Валидатор", d.PubKey.ToString())); rows.Add(("Сумма", A(d.Value, d.Coin)));
                break;
            case UnbondData u:
                title = $"Вывод из стейка {symbol(u.Coin)}"; subtitle = $"{A(u.Value, u.Coin)} у {History.Short(u.PubKey)}";
                rows.Add(("Валидатор", u.PubKey.ToString())); rows.Add(("Сумма", $"{A(u.Value, u.Coin)} — вернётся на баланс после срока вывода"));
                break;
            case MoveStakeData m:
                title = $"Перенос стейка {symbol(m.Coin)}"; subtitle = $"{History.Short(m.FromPubKey)} → {History.Short(m.ToPubKey)}";
                rows.Add(("Сумма", A(m.Value, m.Coin))); rows.Add(("Откуда", m.FromPubKey.ToString())); rows.Add(("Куда", m.ToPubKey.ToString()));
                break;
            case RedeemCheckData chk:
                Check? check = null;
                try { check = Check.Decode(chk.RawCheck); } catch (FormatException) { }
                title = mine ? "Погашение чека" : "Ваш чек погашен"; subtitle = mine ? "" : $"получатель {History.Short(from)}";
                if (check != null) { if (mine) In(check.Value, check.Coin); else Out(check.Value, check.Coin); rows.Add(("Чек", A(check.Value, check.Coin))); }
                break;
            case AddLiquidityData l:
                title = $"Добавление ликвидности {symbol(l.Coin0)}/{symbol(l.Coin1)}";
                subtitle = $"пул {symbol(l.Coin0)}/{symbol(l.Coin1)}";
                Out(l.Volume0, l.Coin0);
                if (Tag("tx.volume1") is { } vol1) Out(vol1, l.Coin1);
                else Out(l.MaximumVolume1, l.Coin1, "≤");
                if (Tag("tx.pool_token_id") is { } lpIn && Tag("tx.liquidity") is { } got) In(got, (uint)lpIn);
                rows.Add(("Внесено", $"{A(l.Volume0, l.Coin0)} + {(Tag("tx.volume1") is { } v1 ? A(v1, l.Coin1) : $"не больше {A(l.MaximumVolume1, l.Coin1)}") }"));
                break;
            case RemoveLiquidityData l:
                title = $"Вывод ликвидности {symbol(l.Coin0)}/{symbol(l.Coin1)}";
                subtitle = $"пул {symbol(l.Coin0)}/{symbol(l.Coin1)}";
                if (Tag("tx.pool_token_id") is { } lpOut) Out(l.Liquidity, (uint)lpOut);
                if (Tag("tx.volume0") is { } r0) In(r0, l.Coin0);
                else In(l.MinimumVolume0, l.Coin0, "≥");
                if (Tag("tx.volume1") is { } r1) In(r1, l.Coin1);
                else In(l.MinimumVolume1, l.Coin1, "≥");
                rows.Add(("Получено", $"{(Tag("tx.volume0") is { } rv0 ? A(rv0, l.Coin0) : $"не меньше {A(l.MinimumVolume0, l.Coin0)}")} + {(Tag("tx.volume1") is { } rv1 ? A(rv1, l.Coin1) : $"не меньше {A(l.MinimumVolume1, l.Coin1)}") }"));
                break;
            case CreateSwapPoolData p:
                title = $"Создание пула {symbol(p.Coin0)}/{symbol(p.Coin1)}";
                subtitle = $"пул {symbol(p.Coin0)}/{symbol(p.Coin1)}";
                Out(p.Volume0, p.Coin0); Out(p.Volume1, p.Coin1);
                rows.Add(("Внесено", $"{A(p.Volume0, p.Coin0)} + {A(p.Volume1, p.Coin1)}"));
                if (Rate(p.Volume0, p.Coin0, p.Volume1, p.Coin1, symbol) is { Length: > 0 } initRate) rows.Add(("Начальный курс", initRate));
                break;
            case MintTokenData t: title = "Выпуск"; subtitle = symbol(t.Coin); In(t.Value, t.Coin); break;
            case BurnTokenData t: title = "Сжигание"; subtitle = symbol(t.Coin); Out(t.Value, t.Coin); break;
            case LockData l: title = "Блокировка"; subtitle = $"до блока {l.DueBlock}"; Out(l.Value, l.Coin); break;
            case AddLimitOrderData o:
                title = $"Лимит-ордер {symbol(o.CoinToSell)} → {symbol(o.CoinToBuy)}";
                subtitle = $"продать за {A(o.ValueToBuy, o.CoinToBuy)}";
                Out(o.ValueToSell, o.CoinToSell);
                In(o.ValueToBuy, o.CoinToBuy, "ожидается");
                rows.Add(("Продажа", A(o.ValueToSell, o.CoinToSell)));
                rows.Add(("Покупка", A(o.ValueToBuy, o.CoinToBuy)));
                if (Rate(o.ValueToSell, o.CoinToSell, o.ValueToBuy, o.CoinToBuy, symbol) is { Length: > 0 } ordR) rows.Add(("Курс ордера", ordR));
                break;
            case RemoveLimitOrderData ro:
                title = $"Отмена лимит-ордера #{ro.Id}";
                subtitle = $"ордер #{ro.Id}";
                rows.Add(("ID ордера", ro.Id.ToString()));
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

    private static string Rate(BigInteger inAmount, uint inCoin, BigInteger outAmount, uint outCoin, Func<uint, string> symbol)
    {
        if (inAmount <= BigInteger.Zero || outAmount <= BigInteger.Zero) return "";
        var one = BigInteger.Pow(10, 18);
        if (inAmount >= outAmount)
        {
            var rate = inAmount * one / outAmount;
            return $"1 {symbol(outCoin)} ≈ {ShortCoins(rate)} {symbol(inCoin)}";
        }
        else
        {
            var rate = outAmount * one / inAmount;
            return $"1 {symbol(inCoin)} ≈ {ShortCoins(rate)} {symbol(outCoin)}";
        }

        static string ShortCoins(BigInteger pip)
        {
            var s = Units.FormatCoins(pip);
            var dot = s.IndexOf('.');
            if (dot < 0) return s;
            var frac = s[(dot + 1)..];
            if (frac.Length > 4)
            {
                var keep = s[..dot] == "0" ? Math.Min(frac.Length, frac.Length - frac.TrimStart('0').Length + 4) : 4;
                if (frac.Length > keep) s = s[..dot] + "." + frac[..keep].TrimEnd('0');
            }
            return s.TrimEnd('.');
        }
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
