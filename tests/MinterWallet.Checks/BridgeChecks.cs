using System.Numerics;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet.Checks;

// Minter Hub withdrawals against real ones from the bridge's address (October 2026): the payload byte for byte, EIP-55
// recipients, and what the bridge kept, read from its multisend.
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
            && Bridge.NetworksOf(2065).Count() == 2 && !Bridge.NetworksOf(0).Any(), "USDTE to Ethereum, USDTBSC to BSC, ETH to both, BIP nowhere");

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
        return Task.CompletedTask;
    }
}
