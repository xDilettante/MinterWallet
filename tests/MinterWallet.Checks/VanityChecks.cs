using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet.Checks;

// Vanity addresses: the pattern's rules and odds, and a found key or phrase that really gives the found address.
internal static class VanityChecks
{
    public static async Task Run()
    {
        var hex = "abcdef0123456789abcdef0123456789abcd0149";
        Assert.True(new VanityPattern("abc").Matches(hex) && new VanityPattern(End: "0149").Matches(hex) && new VanityPattern("ab", "49").Matches(hex), "beginning and end");
        Assert.True(new VanityPattern(Anywhere: "0123").Matches(hex) && !new VanityPattern(Anywhere: "fff").Matches(hex), "anywhere");
        Assert.True(!new VanityPattern("abc", Anywhere: "abc").Matches("abc" + new string('0', 37)), "text anywhere is not counted inside the fixed beginning");
        Assert.Equal(256.0, new VanityPattern("a", "b").ExpectedTries, "16 per fixed digit");
        Assert.True(Vanity.Problem("cafe") == null && Vanity.Problem("coffee") is { } p && p.StartsWith("«o»", StringComparison.Ordinal), "hex digits only");

        // Masks, repeats, equal and mirrored ends.
        Assert.True(new VanityMask("abc*").Matches(hex) && new VanityMask("*0149").Matches(hex) && new VanityMask("a?c*0?49").Matches(hex) && !new VanityMask("abd*").Matches(hex), "masks");
        Assert.True(VanityMask.Problem("dead*beef") == null && VanityMask.Problem("dead") != null && VanityMask.Problem("dexd*") != null, "a mask without * is the whole address; no other letters");
        Assert.Equal(Math.Pow(16, 8), new VanityMask("dead*beef").ExpectedTries, "both ends fixed: 16 per digit");
        var sevens = "7777777" + hex[7..];
        Assert.True(new VanityRepeat(VanityPlace.Start, 7).Matches(sevens) && !new VanityRepeat(VanityPlace.Start, 8).Matches(sevens) && new VanityRepeat(VanityPlace.Anywhere, 7).Matches("01" + sevens[..38]), "repeats");
        var equal = "1234" + new string('0', 32) + "1234"; var mirror = "1234" + new string('5', 32) + "4321";
        Assert.True(new VanityEdges(4, false).Matches(equal) && !new VanityEdges(4, true).Matches(equal) && new VanityEdges(4, true).Matches(mirror), "equal and mirrored ends");
        // Popular features of an address met by chance, and how rare they are.
        Assert.Equal(("7 одинаковых в начале", Math.Pow(16, 6)), Vanity.Rate(sevens)!.Value, "a run at the start");
        Assert.Equal(("«deadbeef» в конце", Math.Pow(16, 8)), Vanity.Rate("910a" + "1b2c3d4e5f6a7b8c9d0e1f2a3b4c" + "deadbeef")!.Value, "a word at the end");
        Assert.True(Vanity.Rate("0123456" + "f" + "9a8b7c6d5e4f3a2b1c0d9e8f7a6b5c4d")!.Value.What.StartsWith("последовательность 0123456", StringComparison.Ordinal), "a sequence");
        Assert.True(Vanity.Rate("1928374655647382910a1b2c3d4e5f6a7b8c9d0e") == null, "an ordinary address");
        Assert.True(Math.Abs(Vanity.Chance(1000, 1000) - (1 - Math.Exp(-1))) < 1e-9, "the chance within the expected tries is 63 %");

        var key = await Vanity.FindAsync(new VanityPattern("a"), VanitySecret.PrivateKey);
        using (var k = new PrivateKey(key!.PrivateKey!)) Assert.True(k.Address == key.Address && key.Address.ToString().StartsWith("Mxa", StringComparison.Ordinal), "the found key gives the address");
        var phrase = await Vanity.FindAsync(new VanityPattern(End: "e"), VanitySecret.Mnemonic12);
        Assert.True(phrase!.Mnemonic!.Split(' ').Length == 12 && Mnemonic.IsValid(phrase.Mnemonic), "a valid 12-word phrase");
        using (var k = HdKey.FromMnemonic(phrase.Mnemonic, 0)) Assert.True(k.Address == phrase.Address && phrase.Address.ToString().EndsWith('e'), "the found phrase gives the address as its № 1");
        using var cancel = new CancellationTokenSource(200);
        Assert.True(await Vanity.FindAsync(new VanityPattern(new string('0', 10)), VanitySecret.PrivateKey, ct: cancel.Token) == null, "stopped without a result");
    }
}
