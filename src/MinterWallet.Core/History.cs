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
            MultisendData m when from == me => $"Мультисенд: {m.List.Count} {(m.List.Count % 10 == 1 && m.List.Count % 100 != 11 ? "получатель" : m.List.Count % 10 is >= 2 and <= 4 && m.List.Count % 100 is < 12 or > 14 ? "получателя" : "получателей")}",
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
