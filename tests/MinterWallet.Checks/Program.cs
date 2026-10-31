using MinterWallet.Checks;

if (args is ["--devnet", var devnet]) return await DevnetWalletChecks.Run(devnet);

var groups = new (string Name, Func<Task> Run)[]
{
    ("derivation paths and storage", DerivationChecks.PathsAndStorage),
    ("argon2id vectors", KeystoreChecks.Argon2),
    ("keystore", KeystoreChecks.Store),
    ("keystore tampering", KeystoreChecks.Tampering),
    ("api v2 parsing", ApiChecks.Parsing),
    ("source rate limits", RateLimitChecks.Run),
    ("send queue", QueueChecks.Run),
    ("guards", GuardChecks.Run),
    ("history decoding", HistoryChecks.Run),
    ("history explained", ExplainChecks.Run),
    ("history beyond the gate", HistoryCursorChecks.Run),
    ("updates", UpdateChecks.Run),
    ("tls certificate switch", TlsChecks.Run),
    ("vanity addresses", VanityChecks.Run),
    ("minter hub withdrawals", BridgeChecks.Run),
    ("usage pings", UsageChecks.Run),
    ("arrivals", IncomeChecks.Run),
};

var failed = 0;
if (args.Length > 0)
{
    groups = groups.Where(g => args.Contains(g.Name)).ToArray();
    if (groups.Length == 0) { Console.WriteLine("No matching check group."); return 2; }
}
foreach (var (name, run) in groups)
{
    try { await run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}"); }
}
Console.WriteLine(failed == 0 ? $"All {groups.Length} groups passed." : $"{failed} of {groups.Length} groups failed.");
return failed == 0 ? 0 : 1;
