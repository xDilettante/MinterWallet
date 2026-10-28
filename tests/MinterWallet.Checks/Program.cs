using MinterWallet.Checks;

if (args is ["--devnet", var devnet]) return await DevnetWalletChecks.Run(devnet);

var groups = new (string Name, Func<Task> Run)[]
{
    ("argon2id vectors", KeystoreChecks.Argon2),
    ("keystore", KeystoreChecks.Store),
    ("keystore tampering", KeystoreChecks.Tampering),
    ("api v2 parsing", ApiChecks.Parsing),
    ("send queue", QueueChecks.Run),
    ("guards", GuardChecks.Run),
    ("history decoding", HistoryChecks.Run),
    ("history beyond the gate", HistoryCursorChecks.Run),
    ("updates", UpdateChecks.Run),
    ("tls certificate switch", TlsChecks.Run),
    ("vanity addresses", VanityChecks.Run),
    ("minter hub withdrawals", BridgeChecks.Run),
    ("usage pings", UsageChecks.Run),
};

var failed = 0;
foreach (var (name, run) in groups)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}"); }
}
Console.WriteLine(failed == 0 ? $"All {groups.Length} groups passed." : $"{failed} of {groups.Length} groups failed.");
return failed == 0 ? 0 : 1;
