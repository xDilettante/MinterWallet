# Third-party notices

This project is MIT-licensed (`LICENSE`).

`src/Minter.Protocol` is an independent C# implementation of the Minter transaction format, checked against
[minter-go-node](https://github.com/MinterTeam/minter-go-node) v3.3.0 and the official Minter JavaScript libraries
through the fixtures in `tests/Minter.Protocol.Checks/Fixtures`. The wallet's error texts and API v2 response formats
follow minter-go-node (MIT, Copyright (c) 2018, Respective Authors; `licenses/minter-go-node.MIT.txt`).

`src/Minter.Protocol/Resources/bip39-english.txt` is the English word list of
[BIP-39](https://github.com/bitcoin/bips/blob/master/bip-0039/english.txt).

The test tools import other code as packages, not copied: `tools/oracle` and `tools/devnet` use minter-go-node and its
dependencies as Go modules, `tools/js-oracle` uses the Minter JavaScript libraries from npm.

Libraries used as NuGet packages keep their own licences: Avalonia with SkiaSharp and HarfBuzzSharp (MIT),
Avalonia.Fonts.Inter (MIT; the Inter typeface in it is under the SIL Open Font License 1.1),
BouncyCastle.Cryptography (MIT).
