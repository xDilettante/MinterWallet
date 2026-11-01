using Minter.Protocol.Checks;

if (args is ["--devnet", var devnet]) return DevnetChecks.Run(devnet);

var groups = new (string Name, Action Run)[]
{
    ("rlp", RlpChecks.Run),
    ("primitives", PrimitiveChecks.Run),
    ("secp256k1", CryptoChecks.Run),
    ("transactions vs go", OracleChecks.Transactions),
    ("commission vs go", OracleChecks.Commissions),
    ("decode go bytes", OracleChecks.DecodeRoundTrip),
    ("multisig vs go", OracleChecks.Multisig),
    ("checks vs go", OracleChecks.Checks),
    ("checks vs js", OracleChecks.JsChecks),
    ("links vs js", OracleChecks.JsLinks),
    ("mnemonic vs js", MnemonicChecks.Run),
    ("rejections", RejectionChecks.Run),
};

var failed = 0;
foreach (var (name, run) in groups)
{
    try { run(); Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { failed++; Console.WriteLine($"FAIL {name}: {error.Message}"); }
}
Console.WriteLine(failed == 0 ? $"All {groups.Length} groups passed." : $"{failed} of {groups.Length} groups failed.");
return failed == 0 ? 0 : 1;
