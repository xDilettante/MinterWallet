using System.Numerics;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet.Checks;

// Minter Hub against real transfers of October 2026: withdrawals (the payload byte for byte, EIP-55 recipients, what the
// bridge kept, read from its multisend) and deposits (the EIP-155 signature, the transferToChain call of a live deposit on
// BSC, the coin list of hub.mnst.club, the bridge's multisend that credits it).
internal static class BridgeChecks
{
    public static Task Run()
    {
        // Recipients as the live withdrawals carried them (EIP-55).
        Assert.Equal("0xDD56AB2926E580C5CBD788549FB10976AAFb0826", Bridge.Checksum("0xdd56ab2926e580c5cbd788549fb10976aafb0826"), "checksum 1");
        Assert.Equal("0x34fecc60eeba4937fB5007Dd4295E67a17C95E78", Bridge.Checksum("0x34FECC60EEBA4937FB5007DD4295E67A17C95E78"), "checksum 2");
        Assert.Equal("0x91a2349c36E5d144905E041f7CC9dBB721B2359C", Bridge.OwnAddress(Address.Parse("Mx91a2349c36e5d144905e041f7cc9dbb721b2359c")), "own address on BSC");
        Assert.True(Bridge.RecipientProblem("0xdd56ab2926e580c5cbd788549fb10976aafb0826") == null && Bridge.RecipientProblem("0xDD56AB2926E580C5CBD788549FB10976AAFb0826") == null, "lower case and EIP-55 accepted");
        Assert.True(Bridge.RecipientProblem("0xDD56AB2926E580C5CBD788549FB10976AAFB0826") == null, "all upper case accepted");
        Assert.True(Bridge.RecipientProblem("0xDd56AB2926E580C5CBD788549FB10976AAFb0826") is { } bad && bad.Contains("EIP-55", StringComparison.Ordinal), "a broken checksum refused");
        Assert.True(Bridge.RecipientProblem("Mx48c1a76d3d82712962c1938c2d27766257f40f80") != null && Bridge.RecipientProblem("0x1234") != null, "not an 0x address");

        // The payloads of Mt62400f76… (9 USDTBSC to BSC) and Mt126a7096… (148 USDTE to Ethereum).
        Assert.Equal("{\"type\":\"send_to_bsc\",\"recipient\":\"0xDD56AB2926E580C5CBD788549FB10976AAFb0826\"}",
            Bridge.Payload(BridgeNetwork.Bsc, "0xdd56ab2926e580c5cbd788549fb10976aafb0826"), "BSC payload as live");
        Assert.Equal("{\"type\":\"send_to_ethereum\",\"recipient\":\"0x34fecc60eeba4937fB5007Dd4295E67a17C95E78\"}",
            Bridge.Payload(BridgeNetwork.Ethereum, "0x34fecc60eeba4937fb5007dd4295e67a17c95e78"), "Ethereum payload as live");
        Assert.True(Bridge.ReadPayload(Bridge.Payload(BridgeNetwork.Bsc, "0xdd56ab2926e580c5cbd788549fb10976aafb0826")) is (BridgeNetwork.Bsc, _) && Bridge.ReadPayload("hello") == null, "payload read back");
        Assert.True(Bridge.NetworksOf(1993).SequenceEqual([BridgeNetwork.Ethereum]) && Bridge.NetworksOf(3494).SequenceEqual([BridgeNetwork.Bsc])
            && Bridge.NetworksOf(0).Count() == 2 && Bridge.NetworksOf(3475).SequenceEqual([BridgeNetwork.Bsc]) && !Bridge.NetworksOf(3224).Any(),
            "USDTE to Ethereum, USDTBSC to BSC, BIP to both, ETHBSC is 3475, 3224 (an LP token) nowhere");

        // What the bridge kept of 9 USDTBSC: 0.09 (1 %) and 0.027833284790728201 of gas.
        var sender = Address.Parse("Mx48c1a76d3d82712962c1938c2d27766257f40f80");
        var gasTaker = Address.Parse("Mx363bb8fec4e54a686aeb29d7e1368b081487416a");
        var withdrawal = new BridgeTx("Mt62400f76", 40366193, DateTimeOffset.Parse("2026-10-26T15:11:52Z"), (int)TxType.Send, sender, 3494, Units.ParseCoins("9"),
            "{\"type\":\"send_to_bsc\",\"recipient\":\"0xDD56AB2926E580C5CBD788549FB10976AAFb0826\"}", []);
        var other = new BridgeTx("Mtother", 40366210, DateTimeOffset.Parse("2026-10-26T15:13:00Z"), (int)TxType.Multisend, Bridge.Multisig, 0, 0, "",
            [new BridgeOut(Bridge.FeeCollector, 0, Units.ParseCoins("905.067553136581652258"))]);
        var settle = new BridgeTx("Mtsettle", 40366224, DateTimeOffset.Parse("2026-10-26T15:14:21Z"), (int)TxType.Multisend, Bridge.Multisig, 0, 0, "",
            [new BridgeOut(gasTaker, 3494, Units.ParseCoins("0.027833284790728201")), new BridgeOut(Bridge.FeeCollector, 3494, Units.ParseCoins("0.09"))]);
        var activity = new[] { settle, other, withdrawal };
        Assert.Equal(new BridgeSettlement(Units.ParseCoins("0.09"), Units.ParseCoins("0.027833284790728201"), "Mtsettle", 40366224), Bridge.SettlementOf(withdrawal, activity), "the 1 % and the gas");
        Assert.True(Bridge.RecentGas(activity, 3494, BridgeNetwork.Bsc) is [var gas] && gas == Units.ParseCoins("0.027833284790728201") && Bridge.RecentGas(activity, 3494, BridgeNetwork.Ethereum).Count == 0,
            "recent gas per network");
        Assert.True(Bridge.SettlementOf(withdrawal with { Value = Units.ParseCoins("10") }, activity) == null, "no settlement without its exact 1 %");
        Deposits();
        return Task.CompletedTask;
    }

    private static void Deposits()
    {
        // EIP-155's own example: nonce 9, 20 gwei, 21000 gas, 1 ether to 0x3535…, chain 1, key 0x4646….
        using var key = new PrivateKey(Enumerable.Repeat((byte)0x46, 32).ToArray());
        var example = new EvmTx(9, 20_000_000_000, 21000, Convert.FromHexString("3535353535353535353535353535353535353535"), BigInteger.Pow(10, 18), []);
        Assert.Equal("daf5a779ae972f972197303d7b574746c7ef83eadac0f2791ad23db92e4c8e53", Convert.ToHexStringLower(example.SigningHash(1)), "EIP-155 signing hash");
        Assert.Equal("f86c098504a817c800825208943535353535353535353535353535353535353535880de0b6b3a76400008025a028ef61340bd939bc2195fe537567866003e1a15d3c71ff63e1590620aa636276a067cbe9d8997f761aecb703304b3800ccf555c9f3dc64214b297fb1966a3b6d83",
            Convert.ToHexStringLower(example.Sign(key, 1)), "EIP-155 signed transaction");

        // 0xc6ab3580… on BSC (27.10.2026): 19 USDT to Mxc25dc727… — the call the wallet builds, byte for byte.
        var usdt = new BridgeToken(3494, "usdtb", BridgeNetwork.Bsc, "0x55d398326f99059ff775485246999027b3197955", 18);
        Assert.Equal("7ecccecc", Convert.ToHexStringLower(Abi.Selector("transferToChain(address,bytes32,bytes32,uint256,uint256)")), "transferToChain selector");
        Assert.Equal("7ecccecc"
            + "00000000000000000000000055d398326f99059ff775485246999027b3197955"
            + "6d696e7465720000000000000000000000000000000000000000000000000000"
            + "000000000000000000000000c25dc727e18ccc4895120a29ec93f11472f9cb8c"
            + "00000000000000000000000000000000000000000000000107ad8f556c6c0000"
            + "0000000000000000000000000000000000000000000000000000000000000000",
            Convert.ToHexStringLower(Bridge.DepositCall(usdt, Address.Parse("Mxc25dc727e18ccc4895120a29ec93f11472f9cb8c"), EvmUnits.Parse("19", 18))), "deposit call as live");
        Assert.Equal("095ea7b3000000000000000000000000f5b0ed82a0b3e11567081694cc66c3df133f7c8f0000000000000000000000000000000000000000000000000000000000000005",
            Convert.ToHexStringLower(Bridge.ApproveCall(Bridge.ContractOf(BridgeNetwork.Bsc), 5)), "approve call");
        Assert.Equal("d0e30db0", Convert.ToHexStringLower(Bridge.WrapCall()), "WETH/WBNB deposit()");

        // Amounts in the token's decimals and the same amount on Minter (18).
        Assert.Equal(new BigInteger(12_340_000), EvmUnits.Parse("12.34", 6), "6 decimals");
        Assert.Equal("12.34", EvmUnits.Format(12_340_000, 6), "formatted back");
        Assert.Equal(Units.ParseCoins("12.34"), EvmUnits.ToMinter(12_340_000, 6), "USDT on Minter");
        Assert.Throws<FormatException>(() => EvmUnits.Parse("1.0000001", 6), "more decimals than the token has");
        Assert.Throws<FormatException>(() => EvmUnits.Parse("1e5", 18), "not a plain number");

        // hub.mnst.club/bridge-api/tokens?chain_id=bsc as it answers; entries without both ends or with a broken address are skipped.
        var list = Bridge.ParseTokens("""
            {"tokens":[{"denom":"bnb","chains":{"bsc":{"token_id":"0xbb4CdB9CBd36B01bD1cBaEBF2De08d9173bc095c","decimals":"18"},"minter":{"token_id":"2107","decimals":"18"}}},
            {"denom":"tonbsc","chains":{"bsc":{"token_id":"0x76A797A59Ba2C17726896976B7B3747BfD1d220f","decimals":"9"},"minter":{"token_id":"3476","decimals":"18"}}},
            {"denom":"usdt","chains":{"ethereum":{"token_id":"0xdAC17F958D2ee523a2206206994597C13D831ec7","decimals":"6"},"minter":{"token_id":"1993"}}},
            {"denom":"bad","chains":{"bsc":{"token_id":"0x1234","decimals":"18"},"minter":{"token_id":"7"}}}]}
            """, BridgeNetwork.Bsc);
        Assert.Equal("bnb:2107:18,tonbsc:3476:9", string.Join(",", list.Select(t => $"{t.Denom}:{t.MinterId}:{t.Decimals}")), "BSC tokens parsed");
        Assert.True(Bridge.IsWrappedNative(list[0]) && !Bridge.IsWrappedNative(list[1]), "WBNB is the wrapped native coin");

        // The bridge's multisend of 27.10.2026 that credited the deposit: 18.81 USDTBSC (1 % kept).
        var recipient = Address.Parse("Mxc25dc727e18ccc4895120a29ec93f11472f9cb8c");
        var credit = new BridgeTx("Mtfd2ad770", 40389000, DateTimeOffset.Parse("2026-10-27T17:28:33Z"), (int)TxType.Multisend, Bridge.Multisig, 0, 0, "",
            [new BridgeOut(Address.Parse("Mx363bb8fec4e54a686aeb29d7e1368b081487416a"), 3494, 1), new BridgeOut(recipient, 3494, Units.ParseCoins("18.81"))]);
        Assert.Equal(Units.ParseCoins("18.81"), Bridge.ArrivalOf(recipient, 3494, DateTimeOffset.Parse("2026-10-27T17:20:00Z"), [credit])?.Value, "deposit credited");
        Assert.True(Bridge.ArrivalOf(recipient, 3494, DateTimeOffset.Parse("2026-10-27T17:30:00Z"), [credit]) == null, "an older multisend is not this deposit");
    }
}
