using System.Collections.Concurrent;
using System.Numerics;
using System.Text.Json;
using Minter.Protocol;

namespace MinterWallet.Core;

public enum TxState { Sent, Included, FailedInBlock, Rejected, Lost }

public sealed record TxOutcome(TxState State, string Hash, long Height = 0, uint Code = 0, string Log = "")
{
    public bool Succeeded => State == TxState.Included;
}

// One transaction per sender at a time: CheckTx refuses a second transaction of a sender while the first is in the
// mempool (code 113). A sender's turn lasts from taking the nonce until the block includes the transaction or the wait
// ends; turns are first come, first served (a chain of tasks per sender), so transactions go out in the order the user
// submitted them. The nonce always comes from the source's latest state (sent so far + 1).
public sealed class SendQueue(IChainBackend backend, TimeSpan? inclusionTimeout = null, TimeSpan? pollInterval = null)
{
    private readonly Dictionary<Address, Task> tails = [];
    private readonly Dictionary<Address, int> waiting = [];
    private readonly TimeSpan timeout = inclusionTimeout ?? TimeSpan.FromSeconds(90);
    private readonly TimeSpan poll = pollInterval ?? TimeSpan.FromSeconds(1);

    public bool IsBusy(Address sender) { lock (tails) return waiting.GetValueOrDefault(sender) > 0; }

    public async Task<TxOutcome> SubmitAsync(Address sender, Func<ulong, Transaction> build, IProgress<TxOutcome>? progress = null, CancellationToken ct = default)
    {
        var turn = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task previous;
        lock (tails)
        {
            previous = tails.GetValueOrDefault(sender) ?? Task.CompletedTask;
            tails[sender] = turn.Task;
            waiting[sender] = waiting.GetValueOrDefault(sender) + 1;
        }
        try
        {
            await previous.WaitAsync(ct);
            var nonce = (await backend.AccountAsync(sender, ct)).Nonce + 1;
            var tx = build(nonce);
            if (tx.Sender() != sender) throw new InvalidOperationException("Транзакция подписана не тем счётом.");
            var answered = true;
            try
            {
                var sent = await backend.SendAsync(tx, ct);
                if (sent.Code != 0) return new TxOutcome(TxState.Rejected, tx.Hash, Code: sent.Code, Log: sent.Log);
            }
            catch (Exception e) when (!ct.IsCancellationRequested && AnswerLost(e))
            {
                // The source may have taken the transaction and lost only its answer (a gate still writing to slow peers, a
                // dropped connection): the transaction is looked for by its hash like any other.
                answered = false;
            }
            progress?.Report(new TxOutcome(TxState.Sent, tx.Hash));
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                await Task.Delay(poll, ct);
                TxRecord? record;
                try { record = await backend.TransactionAsync(tx.Hash, ct); }
                catch (Exception e) when (!ct.IsCancellationRequested && Transient(e)) { continue; }   // one slow answer is not the outcome
                if (record == null) continue;
                return new TxOutcome(record.Code == 0 ? TxState.Included : TxState.FailedInBlock, tx.Hash, record.Height, record.Code, record.Log);
            }
            return new TxOutcome(TxState.Lost, tx.Hash, Log: answered
                ? "Транзакция не попала в блок за отведённое время; она может ещё оставаться в мемпуле."
                : "Источник не ответил на отправку, и в блоках транзакции пока нет. Если она не дошла до сети, баланс не изменится — тогда отправьте ещё раз.");
        }
        finally
        {
            lock (tails)
            {
                waiting[sender]--;
                if (tails.GetValueOrDefault(sender) == turn.Task) tails.Remove(sender);
            }
            turn.SetResult();
        }
    }

    // The request may have reached the source: a timeout, a connection cut after sending, a proxy's "gateway timeout".
    // Not when the connection never opened (refused, no such host, TLS): then nothing was sent.
    private static bool AnswerLost(Exception e) => e switch
    {
        TaskCanceledException or IOException => true,
        HttpRequestException { HttpRequestError: HttpRequestError.ResponseEnded or HttpRequestError.InvalidResponse } => true,
        HttpRequestException { HttpRequestError: HttpRequestError.Unknown, InnerException: IOException } => true,
        ChainApiException api => api.Code == 504,
        _ => false,
    };

    private static bool Transient(Exception e) => e is TaskCanceledException or IOException or HttpRequestException or ChainApiException;
}

public sealed class GasPriceTooHighException(uint required, uint cap)
    : Exception($"Сеть требует gas price {required}, а потолок кошелька — {cap}. Подтвердите повышение потолка или подождите.")
{
    public uint Required { get; } = required;
}

// The gas price multiplies the whole commission; the source names the minimum, the wallet refuses anything above its cap
// unless the user raises the cap explicitly.
public sealed class GasPolicy
{
    public uint Cap { get; set; } = 10;
    public uint Choose(uint minimum) => minimum <= Cap ? Math.Max(1u, minimum) : throw new GasPriceTooHighException(minimum, Cap);
}

public sealed record CoinConflict(string Symbol, uint KnownId, uint NewId);

// Symbol -> id as first seen, remembered across runs (coins.json). A source that suddenly maps a known ticker to another
// id is either lying or reporting a recreated coin; either way the user must see the id before signing.
public sealed class CoinRegistry
{
    private readonly string? path;
    private readonly Dictionary<string, uint> ids = new(StringComparer.Ordinal) { ["BIP"] = 0, ["MNT"] = 0 };
    private readonly Dictionary<uint, string> symbols = new() { [0] = "BIP" };

    public CoinRegistry(string? path = null)
    {
        this.path = path;
        if (path == null || !File.Exists(path)) return;
        foreach (var (symbol, id) in JsonSerializer.Deserialize(File.ReadAllBytes(path), CoreJson.Default.DictionaryStringUInt32) ?? [])
        { ids[symbol] = id; symbols.TryAdd(id, symbol); }
    }

    public string Symbol(uint id) => symbols.TryGetValue(id, out var symbol) ? symbol : $"#{id}";

    public CoinConflict? Observe(CoinRef coin)
    {
        if (ids.TryGetValue(coin.Symbol, out var known) && known != coin.Id) return new CoinConflict(coin.Symbol, known, coin.Id);
        var changed = ids.TryAdd(coin.Symbol, coin.Id);
        symbols[coin.Id] = coin.Symbol;
        if (changed) Save();
        return null;
    }

    public void Accept(CoinConflict conflict) { ids[conflict.Symbol] = conflict.NewId; symbols[conflict.NewId] = conflict.Symbol; Save(); }

    private void Save()
    {
        if (path == null) return;
        WalletPaths.WriteAtomic(path, JsonSerializer.SerializeToUtf8Bytes(ids, CoreJson.Default.DictionaryStringUInt32), secret: false);
    }
}

public sealed record TxPreview(ITxData Data, uint GasCoin, uint GasPrice, byte[] Payload, BigInteger Commission, CoinRef CommissionCoin,
    BigInteger PriceInVoteCoin, uint VoteCoin);

// What the UI uses: preview (commission in the chosen gas coin) and send through the per-sender queue. The key is opened
// for the signature only and disposed right after.
public sealed class WalletService(IChainBackend backend, KeystoreSession session, byte chainId, GasPolicy? gas = null, SendQueue? queue = null)
{
    public IChainBackend Backend { get; } = backend;
    public byte ChainId { get; } = chainId;
    public GasPolicy Gas { get; } = gas ?? new GasPolicy();
    public SendQueue Queue { get; } = queue ?? new SendQueue(backend);

    public async Task<TxPreview> PreviewAsync(WalletAccount account, ITxData data, uint gasCoin = 0, byte[]? payload = null, CancellationToken ct = default)
    {
        payload ??= [];
        if (payload.Length > Transaction.MaxPayload) throw new ArgumentException($"Payload не больше {Transaction.MaxPayload} байт.");
        // A check is redeemed only at gas price 1 (RedeemCheckData refuses any other).
        var gasPrice = data is RedeemCheckData ? 1u : Gas.Choose(await Backend.MinGasPriceAsync(ct));
        var prices = await Backend.PricesAsync(ct);
        var nonce = (await Backend.AccountAsync(account.Address, ct)).Nonce + 1;
        // Estimate on a copy signed by a throwaway key: the source never sees a valid signature before confirmation.
        var probe = Transaction.Create(nonce, ChainId, gasPrice, gasCoin, data, payload);
        using (var throwaway = PrivateKey.Generate()) probe.Sign(throwaway);
        var commission = await Backend.EstimateCommissionAsync(probe, ct);
        var coin = await Backend.CoinAsync(gasCoin, ct);
        var price = Commission.WithGasPrice(Commission.Price(data, payload.Length, prices), gasPrice);
        return new TxPreview(data, gasCoin, gasPrice, payload, commission, new CoinRef(gasCoin, coin?.Symbol ?? $"#{gasCoin}"), price, prices.Coin);
    }

    public Task<TxOutcome> SendAsync(WalletAccount account, TxPreview preview, IProgress<TxOutcome>? progress = null, CancellationToken ct = default) =>
        Queue.SubmitAsync(account.Address, nonce =>
        {
            using var key = session.OpenKey(account);
            var tx = Transaction.Create(nonce, ChainId, preview.GasPrice, preview.GasCoin, preview.Data, preview.Payload);
            tx.Sign(key);
            return tx;
        }, progress, ct);

    // A transaction of a multisig address: built by a member with the address's next nonce, then signed by members in
    // turn (SignMultisig) as it travels between them (MultisigText), sent once their weights reach the threshold.
    public async Task<(Transaction Tx, TxPreview Preview)> ProposeMultisigAsync(Address multisig, ITxData data, uint gasCoin = 0, byte[]? payload = null, CancellationToken ct = default)
    {
        payload ??= [];
        if (payload.Length > Transaction.MaxPayload) throw new ArgumentException($"Payload не больше {Transaction.MaxPayload} байт.");
        var gasPrice = Gas.Choose(await Backend.MinGasPriceAsync(ct));
        var prices = await Backend.PricesAsync(ct);
        var nonce = (await Backend.AccountAsync(multisig, ct)).Nonce + 1;
        var tx = Transaction.Create(nonce, ChainId, gasPrice, gasCoin, data, payload, signatureType: SignatureType.Multi);
        tx.SetMultisigAddress(multisig);
        var probe = tx.Copy();
        using (var throwaway = PrivateKey.Generate()) probe.Sign(throwaway);
        var commission = await Backend.EstimateCommissionAsync(probe, ct);
        var coin = await Backend.CoinAsync(gasCoin, ct);
        var price = Commission.WithGasPrice(Commission.Price(data, payload.Length, prices), gasPrice);
        return (tx, new TxPreview(data, gasCoin, gasPrice, payload, commission, new CoinRef(gasCoin, coin?.Symbol ?? $"#{gasCoin}"), price, prices.Coin));
    }

    // An Ethereum or BSC transaction (the bridge's deposits) signed by the account's key: the same address there.
    public byte[] SignEvm(WalletAccount account, EvmTx tx, ulong chainId)
    {
        using var key = session.OpenKey(account);
        return tx.Sign(key, chainId);
    }

    public void SignMultisig(WalletAccount member, Transaction tx)
    {
        if (tx.SignatureType != SignatureType.Multi) throw new InvalidOperationException("Это не транзакция общего счёта.");
        if (tx.MultisigSigners().Contains(member.Address)) throw new InvalidOperationException("Этот счёт уже подписал.");
        using var key = session.OpenKey(member);
        tx.Sign(key);
    }

    // Sent as signed: its nonce is part of the signatures, so a nonce the address has used meanwhile ends it.
    public Task<TxOutcome> SendMultisigAsync(Transaction tx, IProgress<TxOutcome>? progress = null, CancellationToken ct = default) =>
        Queue.SubmitAsync(tx.Sender(), nonce => nonce == tx.Nonce ? tx
            : throw new InvalidOperationException("Общий счёт уже отправил другую транзакцию с этим номером: соберите эту заново."), progress, ct);

    // Chain ID of a network name: mainnet is 1, any other network (testnet, devnet) uses 2.
    public static byte ChainIdOf(string network) => network.StartsWith("minter-mainnet", StringComparison.Ordinal) ? Minter.Protocol.ChainId.Mainnet : Minter.Protocol.ChainId.Testnet;
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(WriteIndented = true)]
[System.Text.Json.Serialization.JsonSerializable(typeof(Dictionary<string, uint>))]
internal partial class CoreJson : System.Text.Json.Serialization.JsonSerializerContext;

// A partly signed multisig transaction as text: "msig:" and the hex of its bytes; read back with or without the prefix.
public static class MultisigText
{
    public const string Prefix = "msig:";
    public static string Of(Transaction tx) => Prefix + Convert.ToHexStringLower(tx.Encode());

    public static Transaction? Parse(string text)
    {
        var t = string.Concat(text.Where(c => !char.IsWhiteSpace(c)));
        if (t.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase)) t = t[Prefix.Length..];
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        if (t.Length == 0 || t.Length % 2 != 0 || !t.All(Uri.IsHexDigit)) return null;
        try
        {
            var tx = Transaction.Decode(Convert.FromHexString(t));
            return tx.SignatureType == SignatureType.Multi ? tx : null;
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException) { return null; }
    }
}
