using System.Globalization;
using System.Numerics;
using System.Text;
using Minter.Protocol;

namespace MinterWallet.Core;

public enum Direction { Outgoing, Incoming, Self }

public sealed record HistoryEntry(string Hash, long Height, bool Success, string Log, TxType Type, Address From, Direction Direction, string Summary, string Payload);

// History decoded by the wallet itself from the raw bytes the source returns (raw_tx), not from the source's JSON view.
public static class History
{
    public static HistoryEntry Decode(TxRecord record, Address me, Func<uint, string> symbol)
    {
        var tx = Transaction.Decode(record.Raw);
        var from = tx.Sender();
        ITxData? data = null;
        try { data = tx.DecodeData(); } catch (FormatException) { }
        var direction = from == me ? (Recipients(data).Contains(me) ? Direction.Self : Direction.Outgoing) : Direction.Incoming;
        var payload = tx.Payload.Length == 0 ? "" : DecodePayload(tx.Payload);
        return new HistoryEntry(record.Hash, record.Height, record.Code == 0, record.Log, tx.Type, from, direction,
            data == null ? $"Тип 0x{(byte)tx.Type:X2}" : Describe(data, from, me, symbol), payload);
    }

    public static IEnumerable<Address> Recipients(ITxData? data) => data switch
    {
        SendData s => [s.To],
        MultisendData m => m.List.Select(i => i.To),
        _ => []
    };

    public static string Describe(ITxData data, Address from, Address me, Func<uint, string> symbol)
    {
        string A(BigInteger v, uint coin) => $"{Units.FormatCoins(v)} {symbol(coin)}";
        string Route(IEnumerable<uint> coins) => string.Join(" → ", coins.Select(symbol));
        return data switch
        {
            SendData s when from == me => $"Перевод {A(s.Value, s.Coin)} → {Short(s.To)}",
            SendData s => $"Получено {A(s.Value, s.Coin)} от {Short(from)}",
            MultisendData { List: [var only] } when from == me => $"Мультисенд {A(only.Value, only.Coin)} → {Short(only.To)}",
            MultisendData m when from == me => $"Мультисенд: {m.List.Count} {(m.List.Count % 10 is >= 2 and <= 4 && m.List.Count % 100 is < 12 or > 14 ? "получателя" : "получателей")}, "
                + string.Join(", ", m.List.GroupBy(i => i.Coin).Select(g => A(g.Aggregate(BigInteger.Zero, (s, i) => s + i.Value), g.Key))),
            MultisendData m => "Получено " + string.Join(", ", m.List.Where(i => i.To == me).Select(i => A(i.Value, i.Coin))) + $" от {Short(from)} (мультисенд)",
            SellCoinData s => $"Обмен (резерв): {A(s.ValueToSell, s.CoinToSell)} → {symbol(s.CoinToBuy)}",
            SellAllCoinData s => $"Обмен всего (резерв): {symbol(s.CoinToSell)} → {symbol(s.CoinToBuy)}",
            BuyCoinData b => $"Покупка (резерв): {A(b.ValueToBuy, b.CoinToBuy)} за {symbol(b.CoinToSell)}",
            SellSwapPoolData s => $"Обмен через пулы: {A(s.ValueToSell, s.Coins[0])}, {Route(s.Coins)}",
            BuySwapPoolData b => $"Покупка через пулы: {A(b.ValueToBuy, b.Coins[^1])}, {Route(b.Coins)}",
            SellAllSwapPoolData s => $"Обмен всего через пулы: {Route(s.Coins)}",
            DelegateData d => $"Делегирование {A(d.Value, d.Coin)} валидатору {Short(d.PubKey)}",
            UnbondData u => $"Вывод из стейка {A(u.Value, u.Coin)} у {Short(u.PubKey)}",
            MoveStakeData m => $"Перенос стейка {A(m.Value, m.Coin)}: {Short(m.FromPubKey)} → {Short(m.ToPubKey)}",
            LockStakeData => "Блокировка стейка",
            LockData l => $"Блокировка {A(l.Value, l.Coin)} до блока {l.DueBlock}",
            RedeemCheckData => from == me ? "Погашение чека" : $"Погашение чека от {Short(from)}",
            CreateCoinData c => $"Создание монеты {c.Symbol}",
            RecreateCoinData c => $"Пересоздание монеты {c.Symbol}",
            CreateTokenData t => $"Создание токена {t.Symbol}",
            RecreateTokenData t => $"Пересоздание токена {t.Symbol}",
            MintTokenData m => $"Выпуск {A(m.Value, m.Coin)}",
            BurnTokenData b => $"Сжигание {A(b.Value, b.Coin)}",
            EditCoinOwnerData e => $"Смена владельца {e.Symbol} → {Short(e.NewOwner)}",
            CreateSwapPoolData p => $"Создание пула {symbol(p.Coin0)}/{symbol(p.Coin1)}",
            AddLiquidityData l => $"Добавление ликвидности {symbol(l.Coin0)}/{symbol(l.Coin1)}",
            RemoveLiquidityData l => $"Вывод ликвидности {symbol(l.Coin0)}/{symbol(l.Coin1)}",
            AddLimitOrderData o => $"Лимит-ордер: продать {A(o.ValueToSell, o.CoinToSell)} за {A(o.ValueToBuy, o.CoinToBuy)}",
            RemoveLimitOrderData o => $"Отмена лимит-ордера #{o.Id}",
            CreateMultisigData m => $"Создание мультиподписи {m.Threshold} из {m.Weights.Sum(w => w)}",
            EditMultisigData m => $"Изменение мультиподписи: порог {m.Threshold}",
            DeclareCandidacyData d => $"Заявка в валидаторы {Short(d.PubKey)}",
            EditCandidateData e => $"Изменение кандидата {Short(e.PubKey)}",
            EditCandidatePublicKeyData e => $"Смена ключа кандидата {Short(e.PubKey)}",
            EditCandidateCommissionData e => $"Комиссия кандидата {Short(e.PubKey)}: {e.Commission}%",
            SetCandidateOnlineData s => $"Кандидат {Short(s.PubKey)} включён",
            SetCandidateOfflineData s => $"Кандидат {Short(s.PubKey)} выключен",
            SetHaltBlockData h => $"Голос за остановку на блоке {h.Height}",
            VoteCommissionData v => $"Голос за цены комиссий с блока {v.Height}",
            VoteUpdateData v => $"Голос за обновление {v.Version} с блока {v.Height}",
            _ => data.Type.ToString()
        };
    }

    public static string Short(Address a) { var s = a.ToString(); return $"{s[..6]}…{s[^4..]}"; }
    public static string Short(Pubkey p) { var s = p.ToString(); return $"{s[..6]}…{s[^4..]}"; }

    private static string DecodePayload(byte[] payload)
    {
        try { return new UTF8Encoding(false, true).GetString(payload); }
        catch (DecoderFallbackException) { return "0x" + Convert.ToHexStringLower(payload); }
    }
}

// History newest first, a page at a time: the source until it runs out, then older transactions from a second
// provider (the explorer) below the height where the source's own history begins, each transaction once.
public sealed class HistoryCursor(Func<int, Task<IReadOnlyList<TxRecord>>> source, int perPage,
    Func<int, Task<(IReadOnlyList<TxRecord> Records, bool More)>>? older = null, long olderBelow = long.MaxValue)
{
    private readonly HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
    private int sourcePage, olderPage;
    private bool sourceDone, olderDone = older == null;

    public bool HasMore => !sourceDone || !olderDone;

    public async Task<IReadOnlyList<(TxRecord Record, bool Older)>> NextAsync()
    {
        var result = new List<(TxRecord, bool)>();
        while (result.Count < perPage && HasMore)
        {
            if (!sourceDone)
            {
                var page = await source(++sourcePage);
                // The sent and received lists come merged: fewer than a page means both ran out.
                if (page.Count < perPage) sourceDone = true;
                foreach (var r in page) if (seen.Add(r.Hash)) result.Add((r, false));
            }
            else
            {
                var (records, more) = await older!(++olderPage);
                if (!more || records.Count == 0) olderDone = true;
                foreach (var r in records) if (r.Height < olderBelow && seen.Add(r.Hash)) result.Add((r, true));
            }
        }
        return result;
    }
}

// History as a table for accounting: CSV (RFC 4180), a row per transaction, newest first. Transfers and redeemed checks
// carry the counterparty and the exact amount in coins, signed (+ in, − out); the account's own transactions their
// commission when the source reports it (a node's tags; the explorer's older part has none). Texts written by others
// (messages, error logs) are kept from starting a spreadsheet formula.
public sealed record HistoryRow(TxRecord Record, HistoryEntry Entry, DateTimeOffset? Time);

public static class HistoryTable
{
    public static string Csv(IEnumerable<HistoryRow> rows, Address me, Func<uint, string> symbol, Func<string, string> text)
    {
        var csv = new StringBuilder();
        Line(csv, [text("Дата (UTC)"), text("Блок"), text("Хеш"), text("Статус"), text("Тип"), text("Направление"), text("Контрагент"),
            text("Сумма"), text("Монета"), text("Комиссия"), text("Монета комиссии"), text("Описание"), text("Сообщение")]);
        foreach (var (record, entry, time) in rows)
        {
            ITxData? data = null;
            try { data = Transaction.Decode(record.Raw).DecodeData(); } catch (FormatException) { }
            var (counterparty, amount, coin) = Transfer(data, entry, me, symbol, text);
            string fee = "", feeCoin = "";
            if (entry.From == me && record.Tags.TryGetValue("tx.commission_amount", out var f) && BigInteger.TryParse(f, NumberStyles.None, CultureInfo.InvariantCulture, out var pip))
            {
                fee = Units.FormatCoins(pip);
                feeCoin = record.Tags.TryGetValue("tx.commission_coin", out var c) && uint.TryParse(c, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? symbol(id) : "";
            }
            var status = entry.Success ? text("выполнена") : $"{text("не выполнена")}: {Safe(entry.Log)}";
            var direction = text(entry.Direction switch { Direction.Incoming => "входящая", Direction.Self => "себе", _ => "исходящая" });
            Line(csv, [time?.UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) ?? "", record.Height.ToString(CultureInfo.InvariantCulture),
                record.Hash, status, entry.Type.ToString(), direction, counterparty, amount, coin, fee, feeCoin, Safe(text(entry.Summary)), Safe(entry.Payload)]);
        }
        return csv.ToString();
    }

    private static (string Counterparty, string Amount, string Coin) Transfer(ITxData? data, HistoryEntry entry, Address me, Func<uint, string> symbol, Func<string, string> text)
    {
        static BigInteger Sum(IEnumerable<MultisendItem> items) => items.Aggregate(BigInteger.Zero, (s, i) => s + i.Value);
        switch (data)
        {
            case SendData s:
                var sign = entry.Direction switch { Direction.Incoming => "", Direction.Outgoing => "-", _ => "" };
                return (entry.Direction == Direction.Incoming ? entry.From.ToString() : s.To.ToString(),
                    entry.Direction == Direction.Self ? "0" : sign + Units.FormatCoins(s.Value), symbol(s.Coin));
            case MultisendData m when entry.Direction == Direction.Incoming:
                var mine = m.List.Where(i => i.To == me).ToList();
                return mine.Select(i => i.Coin).Distinct().Count() == 1 ? (entry.From.ToString(), Units.FormatCoins(Sum(mine)), symbol(mine[0].Coin)) : (entry.From.ToString(), "", "");
            case MultisendData m:
                var others = m.List.Where(i => i.To != me).ToList();
                var to = text($"получателей: {others.Count}");
                return others.Count > 0 && others.Select(i => i.Coin).Distinct().Count() == 1 ? (to, "-" + Units.FormatCoins(Sum(others)), symbol(others[0].Coin)) : (to, "", "");
            case RedeemCheckData r when entry.From == me:
                try
                {
                    var check = Check.Parse("Mc" + Convert.ToHexStringLower(r.RawCheck));
                    return (check.Issuer().ToString(), Units.FormatCoins(check.Value), symbol(check.Coin));
                }
                catch (Exception e) when (e is FormatException or ArgumentException or InvalidOperationException) { return ("", "", ""); }
            default:
                return ("", "", "");
        }
    }

    private static void Line(StringBuilder csv, IEnumerable<string> fields) => csv.Append(string.Join(',', fields.Select(Quote))).Append("\r\n");

    private static string Quote(string field) =>
        field.IndexOfAny([',', '"', '\r', '\n']) >= 0 ? '"' + field.Replace("\"", "\"\"") + '"' : field;

    // A text from someone else that a spreadsheet would run as a formula (=, +, -, @) gets a leading apostrophe.
    private static string Safe(string field) => field.Length > 0 && field[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + field : field;
}
