using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Minter.Protocol;
using MinterWallet;
using MinterWallet.Core;

// The wallet's own window and view models, headless, against a devnet. First run: create (copy the phrase, skip the
// check, pass it with a pasted phrase), forgot password, restore from a pasted phrase, reveal and copy it in the
// accounts page; more addresses: the next one of the phrase, a new phrase, an address to watch, rename, remove. Then a
// test key: balances, send with the confirmation (password stubbed), history, swap with the coin found by ticker and
// the quote by itself, delegation, multisend, sources in the settings, wrong password, both themes, lock and unlock.
// --mainnet URL: read-only, against a real MinterGate: a public mainnet address to watch, its balances in dollars, its
// history with the older part from the Minter explorer, the gate's coin list for a swap. Nothing is signed.
if (args is not ["--devnet" or "--mainnet", var url]) { Console.WriteLine("Usage: MinterWallet.UiChecks --devnet http://WSL-IP:38843 | --mainnet https://GATE"); return 2; }
var mainnet = args[0] == "--mainnet";
// The checks' wallets send no usage pings to the project's gate.
Environment.SetEnvironmentVariable("MINTERWALLET_USAGE_URL", "off");
var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", mainnet ? "ui-mainnet" : "ui-checks"));
Directory.CreateDirectory(output);
var session = HeadlessUnitTestSession.StartNew(typeof(UiChecksApp));
var code = await session.Dispatch(() => mainnet ? UiScenario.Mainnet(url, output) : UiScenario.Run(url, output), CancellationToken.None);
// The headless session keeps its dispatcher thread (and the wallet's timers) alive: end the process explicitly.
Environment.Exit(code);
return code;

public static class UiChecksApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

public static class UiScenario
{
    private const string Password = "ui-check-пароль";
    private static string output = "";
    private static Window? window;
    private static int step;

    public static async Task<int> Run(string url, string outputDirectory)
    {
        output = outputDirectory;
        foreach (var old in Directory.GetFiles(output, "*.png")) File.Delete(old);
        var first = Directory.CreateTempSubdirectory("minterwallet-ui-").FullName;
        var data = Directory.CreateTempSubdirectory("minterwallet-ui-").FullName;
        try
        {
            Formatting();
            await FirstRun(url, first);
            await Operations(url, data);
            await English(url, Directory.CreateTempSubdirectory("minterwallet-ui-").FullName);
            Console.WriteLine($"PASS ui: {step} screens rendered to {output}; create (copy, skip and paste-check), forgot password, restore from a pasted numbered phrase, " +
                "phrase reveal and copy, next address, new phrase, watch-only, rename, remove; import, balances, send, wrong password, history, swap by ticker with automatic quote, " +
                "every screen in English with no Cyrillic left, the language switched in place, " +
                "liquidity added and halved, limit order placed and cancelled, delegation, multisend, check issued and redeemed, sources, receive with QR, address book, password change, lock/unlock against the devnet; dark and light themes; " +
                "amounts by significant digits, addresses with highlighted edges, the «Вид» switches; a lookalike recipient flagged, sell all, Minter Links, history to CSV; " +
                "a token created with its price shown, minted, burned, re-created, pooled and given away; a 2-of-2 shared account created, funded and spent from; " +
                "a candidate declared, switched on and off, its control address changed, a stake moved to it");
            return 0;
        }
        catch (Exception error)
        {
            Shot("failure");
            Console.WriteLine($"FAIL ui: {error.Message}");
            return 1;
        }
        finally
        {
            window?.Close();
            foreach (var directory in new[] { first, data }) try { Directory.Delete(directory, true); } catch (IOException) { }
        }
    }

    private static void Settings(string directory, string url, string theme, string name = "devnet (WSL)", string language = "ru") =>
        File.WriteAllText(Path.Combine(directory, WalletSettings.FileName), $$"""{"Sources":[{"Name":"{{name}}","Url":"{{url}}"}],"SelectedSource":0,"GasPriceCap":10,"AutoLockMinutes":10,"Theme":"{{theme}}","CheckUpdates":false,"Language":"{{language}}"}""");

    // The same wallet in English: every screen and mode, a notice, the confirmation and the lock screen; no text of the
    // interface may stay Cyrillic (only the name of the Russian language in its switch).
    private static readonly HashSet<string> Untranslated = [];

    private static void Scan(string where, Visual? root = null)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        foreach (var block in (root ?? window!).GetVisualDescendants().OfType<TextBlock>())
            if (block.IsEffectivelyVisible && Shown(block) is { } text && Localization.HasCyrillic(text) && text != "Русский"
                && !(block.DataContext is HistoryItem item && text == item.Payload))   // a transaction's own message is data
                Untranslated.Add($"{where}: {text}");
    }

    private static async Task English(string url, string data)
    {
        Settings(data, url, "dark", "devnet", "en");
        var shell = new Shell(data) { Confirm = _ => Task.FromResult<string?>(Password) };
        window = new MainWindow(shell) { Width = 1100, Height = 720 };
        window.Show();
        Check(Localization.English, "the language setting chose English");
        Scan("welcome"); Shot("en-welcome");
        ((WelcomeViewModel)shell.Page).Create.Execute(null);
        var create = (CreateWalletViewModel)shell.Page;
        Scan("create, phrase"); Shot("en-create");
        create.Next.Execute(null); Scan("create, check");
        create.Skip.Execute(null); create.Password = "short"; Scan("create, password");
        create.Back.Execute(null); create.Back.Execute(null); create.Back.Execute(null);
        ((WelcomeViewModel)shell.Page).ImportKey.Execute(null);
        var import = (ImportViewModel)shell.Page;
        import.Secret = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("minterwallet-test-2")));
        import.Name = "Test B"; import.Password = Password; import.Repeat = Password;
        Scan("import"); Shot("en-import");
        import.Finish.Execute(null);
        var main = await Until(() => shell.Page as MainViewModel, "main screen in English");
        await Until(() => main.Balances.Count > 0 ? main : null, "balances");
        main.ShowReceive = true;
        Scan("overview"); Shot("en-overview");
        main.ShowReceive = false;
        main.Section = Section.Send;
        main.Send.SendNow.Execute(null);
        await Until(() => main.Send.Tone == Tone.Error ? main : null, "a validation notice");
        Scan("send"); Shot("en-send"); main.Send.ModeIndex = 3; Scan("send, bridge"); main.Send.ModeIndex = 0;
        main.Send.ModeIndex = 1; main.Multisend.Lines = "Mx1 2"; Scan("multisend");
        main.Send.ModeIndex = 2; Scan("send by check");
        main.Send.ModeIndex = 0;
        main.Section = Section.Swap; Scan("swap");
        main.TradeModeIndex = 1; await Until(() => !main.Orders.Load.CanExecute(null) ? null : main, "orders loaded"); Scan("orders");
        main.TradeModeIndex = 2; await Until(() => !main.Liquidity.Load.CanExecute(null) ? null : main, "liquidity loaded"); Scan("liquidity"); Shot("en-liquidity");
        main.TradeModeIndex = 0;
        main.Section = Section.Staking;
        await Until(() => main.Staking.Candidates.Count > 0 ? main : null, "candidates");
        Scan("staking");
        main.Section = Section.History;
        await Until(() => main.History.Items.Count > 0 && !main.History.Loading ? main : null, "history");
        Scan("history"); Shot("en-history");
        main.Section = Section.Tokens; Scan("tokens"); Shot("en-tokens");
        main.Tokens.ModeIndex = 1; Scan("tokens, create");
        main.Tokens.ModeIndex = 2; Scan("tokens, pool");
        main.Tokens.ModeIndex = 0;
        main.Section = Section.Staking; main.Staking.ModeIndex = 1; Scan("own validator"); main.Staking.ModeIndex = 0;
        main.Section = Section.Multisig; Scan("shared account"); main.Multisig.ModeIndex = 1; Scan("shared account, transfer"); main.Multisig.ModeIndex = 0;
        main.Section = Section.Accounts; Scan("accounts");
        main.Section = Section.Settings; Scan("settings"); Shot("en-settings");
        var dialog = new ConfirmWindow(new ConfirmRequest("Перевод",
            [("Операция", "Перевод 1.5 MNT → Mxf1e0…f8d5"), ("Получатель", "Mxf1e0f934a415d37018a318c779441143ba20f8d5"), ("Комиссия", "0.068 MNT (id 0), gas price 1"), ("Сеть", "minter-devnet (chain ID 2) · devnet")],
            ["Новый получатель. У адресов Minter нет контрольной суммы: опечатка или подмена в буфере обмена дадут другой действительный адрес. Сверьте адрес целиком: мошенники подбирают адреса с теми же началом и концом."]));
        dialog.Show(); Scan("confirmation", dialog);
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(output, $"{++step:00}-en-confirm.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        dialog.Close();
        // Back to Russian in place: the same page redrawn.
        main.Settings.LanguageIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Check(!Localization.English && window!.GetVisualDescendants().OfType<TextBlock>().Any(b => b.Text == "Настройки"), "switched to Russian without a restart");
        main.Settings.LanguageIndex = 2;
        Dispatcher.UIThread.RunJobs();
        main.LockNow.Execute(null);
        Scan("lock screen"); Shot("en-locked");
        window.Close();
        Check(Untranslated.Count == 0, "untranslated texts:\n  " + string.Join("\n  ", Untranslated));
    }

    // Read-only on mainnet: a busy public address to watch (it sends multisends), nothing signed.
    public static async Task<int> Mainnet(string url, string outputDirectory)
    {
        output = outputDirectory;
        foreach (var old in Directory.GetFiles(output, "*.png")) File.Delete(old);
        var data = Directory.CreateTempSubdirectory("minterwallet-ui-").FullName;
        try
        {
            Settings(data, url, "dark", "MinterGate");
            var shell = new Shell(data);
            window = new MainWindow(shell) { Width = 1100, Height = 720 };
            window.Show();
            ((WelcomeViewModel)shell.Page).Watch.Execute(null);
            var watch = (ImportViewModel)shell.Page;
            watch.Secret = "Mxf4a7739c9956dea74ca4ae87be4c9665bb71a310"; watch.Name = "Наблюдение"; watch.Password = Password; watch.Repeat = Password;
            watch.Finish.Execute(null);
            var main = await Until(() => shell.Page as MainViewModel, "main screen");
            await Until(() => main.Balances.Count > 0 && main.HasUsd ? main : null, "balances in dollars from the gate", 60, () => main.Status);
            Check(shell.GateIndexFrom > 1 && main.Settings.Sources[main.Settings.SourceIndex].Source.Kind == "gate", "the source is a gate with a history index");
            Shot("mainnet-overview");
            main.Section = Section.History;
            await Until(() => main.History.Items.Count > 0 && !main.History.Loading ? main : null, "history", 60, () => main.History.Status);
            for (var i = 0; i < 5 && !main.History.Items.Any(x => x.FromExplorer) && main.History.HasMore; i++)
            {
                var count = main.History.Items.Count;
                main.History.More.Execute(null);
                await Until(() => main.History.Items.Count > count || !main.History.HasMore ? main : null, "more history", 60, () => main.History.Status);
            }
            Check(main.History.Items.Any(x => x.FromExplorer && x.Entry.Height < shell.GateIndexFrom), "history older than the gate's index from the explorer");
            Shot("mainnet-history");
            main.Section = Section.Swap;
            await Until(() => main.HasCatalog ? main : null, "the gate's coin list", 60);
            main.Swap.Buy.Query = "USDT";
            Check(main.Swap.Buy.Results.Any(c => c.Symbol.StartsWith("USDT", StringComparison.Ordinal)), "USDT found by ticker in the gate's list");
            main.Section = Section.Overview; main.ShowReceive = true;
            Shot("mainnet-receive");
            Check(main.Balances.Where(b => b.Coin.Id != 0 && b.BipValue.IsZero).All(b => b.BipText == $"нет оценки в {main.BaseSymbol}"), "a token without a price says so");
            // Out to BSC through Minter Hub, not sent: the bridge's live work gives the gas estimate; the own address there.
            main.Section = Section.Send; main.Send.ModeIndex = 3;
            Check(main.Hub.IsAvailable, "the bridge is offered on mainnet");
            main.Hub.Coin = new BalanceItem(new CoinRef(3494, "USDTBSC"), Units.ParseCoins("50"), 0, main.BaseSymbol);
            Check(main.Hub.NetworkIndex == (int)BridgeNetwork.Bsc, "USDTBSC goes to BSC only");
            main.Hub.Amount = "20";
            await Until(() => main.Hub.Estimate.Contains("Мост удержит 1 % — 0.2 USDTBSC", StringComparison.Ordinal) && !main.Hub.Estimate.Contains("не ответил", StringComparison.Ordinal) ? main : null,
                "the bridge's fees from its latest withdrawals", 60, () => main.Hub.Estimate);
            main.Hub.Own.Execute(null);
            Check(main.Hub.Recipient == Bridge.OwnAddress(main.Selected!.Account.Address) && main.Hub.RecipientCheck.StartsWith("Это ваш адрес в BSC", StringComparison.Ordinal), "the account's own address on BSC");
            Shot("mainnet-bridge");
            Console.WriteLine($"Bridge estimate: {main.Hub.Estimate.Replace('\n', ' ')}");
            Console.WriteLine($"PASS ui mainnet: {main.Balances.Count} coins with dollars ({main.TotalUsd}), {main.History.Items.Count} history items " +
                $"({main.History.Items.Count(x => x.FromExplorer)} from the explorer below {shell.GateIndexFrom}), coin list from the gate; screens in {output}");
            return 0;
        }
        catch (Exception error)
        {
            Shot("failure");
            Console.WriteLine($"FAIL ui mainnet: {error.Message}");
            return 1;
        }
        finally
        {
            window?.Close();
            try { Directory.Delete(data, true); } catch (IOException) { }
        }
    }

    // A new wallet without being forced through anything: copy the phrase, skip the check, come back and pass it with
    // the whole phrase pasted into one field; then forget the password, start over and restore from a pasted copy
    // that has numbering and commas in it; finally reveal and copy the phrase in the settings.
    private static async Task FirstRun(string url, string data)
    {
        Settings(data, url, "dark");
        var clipboard = "";
        var shell = new Shell(data)
        {
            Confirm = _ => Task.FromResult<string?>(Password),
            CopyText = text => { clipboard = text; return Task.CompletedTask; },
            PasteText = () => Task.FromResult<string?>(clipboard)
        };
        window = new MainWindow(shell) { Width = 1100, Height = 720 };
        window.Show();
        Shot("welcome");

        ((WelcomeViewModel)shell.Page).Create.Execute(null);
        var create = (CreateWalletViewModel)shell.Page;
        Shot("create-phrase");
        create.Copy.Execute(null);
        await Until(() => clipboard.Length > 0 ? create : null, "phrase copied");
        var phrase = clipboard;
        Check(phrase == string.Join(' ', create.Words.Select(w => w.Word)) && Mnemonic.IsValid(phrase), "copied phrase is the shown phrase");
        create.Next.Execute(null);
        Check(create.IsStep2, "step 2 after the phrase, with nothing ticked");
        create.Skip.Execute(null);
        Check(create.IsStep3 && !create.Verified, "the check can be skipped");
        create.Back.Execute(null);
        var words = phrase.Split(' ');
        create.Answer2 = string.Join(", ", words.Select((w, i) => $"{i + 1}. {w}"));
        Check(create.Answer1 == words[create.Ask[0]] && create.Answer3 == words[create.Ask[2]], "a pasted phrase fills all three answers");
        Shot("create-check");
        create.Check.Execute(null);
        Check(create.IsStep3 && create.Verified, "check passes with the pasted phrase");
        create.Password = Password; create.Repeat = "другой пароль";
        create.Finish.Execute(null);
        await Until(() => create.Tone == Tone.Error ? create : null, "mismatched passwords refused");
        create.Repeat = Password;
        Shot("create-password");
        create.Finish.Execute(null);
        var main = await Until(() => shell.Page as MainViewModel, "main screen after create");
        using (var key = HdKey.FromMnemonic(phrase)) Check(main.Selected!.Account.Address == key.Address, "created account is the phrase's first address");

        // Forgot the password: the key store is set aside, not deleted, and the phrase restores the same address.
        main.LockNow.Execute(null);
        var unlock = (UnlockViewModel)shell.Page;
        unlock.Forgot.Execute(null);
        Shot("forgot-password");
        unlock.StartOver.Execute(null);
        Check(shell.Page is WelcomeViewModel && Directory.GetFiles(data, Keystore.FileName + ".old-*").Count(f => !f.EndsWith(".bak", StringComparison.Ordinal)) == 1
            && !File.Exists(Path.Combine(data, Keystore.FileName)), "old key store set aside");
        ((WelcomeViewModel)shell.Page).Restore.Execute(null);
        var restore = (ImportViewModel)shell.Page;
        clipboard = string.Join("\n", words.Select((w, i) => $"{i + 1}) {w},"));
        restore.Paste.Execute(null);
        await Until(() => restore.Secret.Length > 0 ? restore : null, "phrase pasted");
        Check(restore.Secret == phrase && restore.SecretTone == Tone.Success, "pasted numbered phrase is cleaned up and valid");
        restore.Password = Password; restore.Repeat = Password;
        Shot("restore");
        restore.Finish.Execute(null);
        main = await Until(() => shell.Page as MainViewModel, "main screen after restore");
        using (var key = HdKey.FromMnemonic(phrase)) Check(main.Selected!.Account.Address == key.Address, "restored the same address");

        // Accounts: the phrase shown and copied by password, then more addresses — the next one of the same phrase,
        // a new phrase, an address to watch — renamed and removed.
        var accounts = main.AccountsPage;
        main.Section = Section.Accounts;
        accounts.Reveal.Execute(null);
        await Until(() => accounts.PhraseShown ? main : null, "phrase revealed", 30, () => accounts.Status);
        clipboard = "";
        accounts.CopyPhrase.Execute(null);
        await Until(() => clipboard.Length > 0 ? main : null, "revealed phrase copied");
        Check(clipboard == phrase, "accounts page copies the phrase");
        Shot("accounts-phrase");
        main.Section = Section.Overview;
        main.AddAddress.Execute(null);
        Check(main.IsAccounts && accounts.IsDerive, "the + button opens adding the next address of the phrase");
        accounts.Add.Execute(null);
        using (var key = HdKey.FromMnemonic(phrase, 1)) Check(main.Selected!.Account.Address == key.Address && main.Accounts.Count == 2, "second address of the phrase, selected");
        // The phrase's addresses by number: № 1 and № 2 in the wallet, № 6 added by its number.
        accounts.ShowDerived.Execute(null);
        await Until(() => accounts.Derived.Count == 10 ? main : null, "the phrase's first ten addresses", 30, () => accounts.Status);
        Check(accounts.Derived[0].InWallet && accounts.Derived[1].InWallet && !accounts.Derived[5].InWallet, "which addresses are in the wallet");
        accounts.AddDerived.Execute(accounts.Derived[5]);
        using (var key = HdKey.FromMnemonic(phrase, 5)) Check(main.Selected!.Account.Address == key.Address && main.Selected.Account.Index == 5 && main.Accounts.Count == 3, "address № 6 added by its number");
        Shot("accounts-derived");
        // A vanity address: a key whose address starts with "ab", then a 12-word phrase whose address ends with "c".
        accounts.AddIndex = (int)AddKind.Vanity;
        accounts.Vanity.TemplateIndex = 0; accounts.Vanity.StartText = "AB";
        Check(accounts.Vanity.StartText == "ab" && accounts.Vanity.Problem == "" && accounts.Vanity.Preview == "Mxab…", "the vanity pattern, lower-cased");
        accounts.Vanity.StartText = "xyz";
        Check(accounts.Vanity.Problem.StartsWith("«x»", StringComparison.Ordinal), "letters beyond a–f refused");
        accounts.Vanity.StartText = "ab";
        accounts.Vanity.Start.Execute(null);
        await Until(() => accounts.Vanity.HasFound ? main : null, "a vanity key found", 60, () => accounts.Vanity.Status + " " + accounts.Vanity.ProgressText);
        Check(accounts.Vanity.Found.StartsWith("Mxab", StringComparison.Ordinal), "the found address starts with Mxab");
        Shot("accounts-vanity");
        var vanityKey = accounts.Vanity.Found;
        accounts.Vanity.Add.Execute(null);
        Check(main.Selected!.Account.Address.ToString() == vanityKey && main.Accounts.Count == 4, "the vanity key added and selected");
        accounts.Vanity.TemplateIndex = 1; accounts.Vanity.EndText = "c"; accounts.Vanity.KindIndex = 1;
        accounts.Vanity.Start.Execute(null);
        await Until(() => accounts.Vanity.HasFound ? main : null, "a vanity phrase found", 120, () => accounts.Vanity.Status + " " + accounts.Vanity.ProgressText);
        Check(accounts.Vanity.Found.EndsWith("c", StringComparison.Ordinal), "the found phrase's address ends with c");
        accounts.Vanity.Add.Execute(null);
        Check(main.Accounts.Count == 5 && main.Shell.Store!.KindOf(main.Selected!.Account) == SecretKind.Mnemonic, "the vanity phrase added as a phrase");
        // A mask: "a" first, "b" third, anything after; and the odds of a long one are shown, not refused.
        accounts.Vanity.TemplateIndex = 4; accounts.Vanity.KindIndex = 0; accounts.Vanity.MaskText = "a?b*";
        Check(accounts.Vanity.Problem == "", "a valid mask");
        accounts.Vanity.Start.Execute(null);
        await Until(() => accounts.Vanity.HasFound ? main : null, "an address by the mask", 60, () => accounts.Vanity.Status);
        Check(accounts.Vanity.Found[2] == 'a' && accounts.Vanity.Found[4] == 'b', $"the mask a?b* matched: {accounts.Vanity.Found}");
        accounts.Vanity.TemplateIndex = 0; accounts.Vanity.StartText = "deadbeefcafe";
        await Until(() => accounts.Vanity.Odds.Contains("Вероятность найти за минуту", StringComparison.Ordinal) ? main : null, "the odds of a long beginning", 30, () => accounts.Vanity.Odds);
        Check(accounts.Vanity.Problem == "" && accounts.Vanity.Odds.Contains("практически недостижим", StringComparison.Ordinal), "a long pattern is allowed with its odds");
        Shot("accounts-vanity-odds");
        accounts.AddIndex = (int)AddKind.NewPhrase;
        accounts.CopyNewPhrase.Execute(null);
        await Until(() => clipboard != phrase ? main : null, "new phrase copied");
        var second = clipboard;
        Check(Mnemonic.IsValid(second) && second == string.Join(' ', accounts.NewWords.Select(w => w.Word)), "new phrase shown and copied");
        ScrollToEnd();
        Shot("accounts-new-phrase");
        accounts.Add.Execute(null);
        using (var key = HdKey.FromMnemonic(second)) Check(main.Selected!.Account.Address == key.Address, "account from the new phrase");
        accounts.AddIndex = (int)AddKind.Watch;
        clipboard = "Mxf1e0f934a415d37018a318c779441143ba20f8d5";
        accounts.Paste.Execute(null);
        await Until(() => accounts.SecretTone == Tone.Success ? main : null, "watch address pasted");
        accounts.Name = "Чужой адрес";
        accounts.Add.Execute(null);
        Check(main.IsWatchOnly && main.Accounts.Count == 7, "watch-only account added");
        accounts.RenameTo = "Наблюдение";
        accounts.Rename.Execute(null);
        Check(main.AccountName == "Наблюдение", "account renamed");
        Shot("accounts");
        accounts.ContactName = "Друг"; accounts.ContactAddress = "Mx26a08b83ff690136b336c050e96d50178edd62de";
        accounts.AddContact.Execute(null);
        Check(main.Contacts.Any(c => c.Name == "Друг"), "contact added by hand");
        accounts.Remove.Execute(null);
        Check(accounts.ConfirmingRemove, "removal asks first");
        accounts.Remove.Execute(null);
        await Until(() => main.Accounts.Count == 6 ? main : null, "account removed", 30, () => accounts.Status);
        window.Close();
    }

    private static async Task Operations(string url, string data)
    {
        // Connection failures name their cause: the date of the certificate, a refused connection (a stale proxy).
        Check(ScreenModel.Describe(new HttpRequestException("The SSL connection could not be established, see inner exception.",
            new System.Security.Authentication.AuthenticationException("The remote certificate is invalid because of errors in the certificate chain: NotTimeValid"))).Contains("по дате", StringComparison.Ordinal), "certificate date named");
        Check(ScreenModel.Describe(new HttpRequestException("No connection", new System.Net.Sockets.SocketException((int)System.Net.Sockets.SocketError.ConnectionRefused))).Contains("прокси", StringComparison.Ordinal), "refused connection explained");
        Settings(data, url, "dark");
        var confirmations = new List<ConfirmRequest>();
        var answer = Password;
        (string Name, string Text)? savedFile = null;
        var shell = new Shell(data)
        {
            Confirm = request => { confirmations.Add(request); return Task.FromResult<string?>(answer); },
            SaveText = (name, text) => { savedFile = (name, text); return Task.FromResult(true); },
        };
        window = new MainWindow(shell) { Width = 1100, Height = 720 };
        window.Show();

        // Import a public TEST key (sha256("minterwallet-test-2")) through the import screen.
        ((WelcomeViewModel)shell.Page).ImportKey.Execute(null);
        var import = (ImportViewModel)shell.Page;
        import.Secret = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("minterwallet-test-2")));
        import.Name = "Тестовый B"; import.Password = Password; import.Repeat = Password;
        Shot("import");
        import.Finish.Execute(null);
        var main = await Until(() => shell.Page as MainViewModel, "main screen after import");
        await Until(() => main.Balances.Count > 0 ? main : null, "balances from the devnet");
        Check(main.Selected!.Account.Address.ToString() == "Mx37980a12b1be0855384b8d4f24b2d7957f680149", "imported address");
        Shot("overview");
        Check(Marked(main.AddressText) is ["Mx", "3798", "0149"], "overview address: the prefix and its beginning and end highlighted");
        main.ToggleReceive.Execute(null);
        Check(main.ShowReceive, "receive panel");
        Shot("receive");
        // Ask for 2.5 MNT with a message: a Minter Link in the QR code, and the same link opened in the send form.
        main.Request.Amount = "2,5"; main.Request.Message = "счёт 7";
        var request = MinterLink.Parse(main.Request.Link);
        Check(main.Request.QrText == main.Request.Link && request?.DecodeData() is SendData { Coin: 0 } asked && asked.To == main.Selected!.Account.Address
            && asked.Value == Units.ParseCoins("2.5") && Encoding.UTF8.GetString(request.Payload) == "счёт 7", "payment request link in the QR code");
        Shot("receive-request");
        main.Send.Recipient = main.Request.Link;
        Dispatcher.UIThread.RunJobs();
        Check(main.Send.Recipient == main.Address && main.Send.Amount == "2.5" && main.Send.Payload == "счёт 7" && main.Send.Coin?.Coin.Id == 0, "the link fills the send form");
        main.Send.Recipient = ""; main.Send.Amount = ""; main.Send.Payload = "";
        main.Request.Amount = ""; main.Request.Message = "";
        Check(main.Request.QrText == main.Address, "the bare address again without an amount");
        main.ToggleReceive.Execute(null);

        // An 0x address in an ordinary transfer points to the bridge tab; the bridge itself is mainnet only.
        main.Section = Section.Send;
        main.Send.Recipient = "0xdd56ab2926e580c5cbd788549fb10976aafb0826";
        Check(main.Send.RecipientCheck.StartsWith("Это адрес в Ethereum или BSC", StringComparison.Ordinal), "an 0x address in a Minter transfer explained");
        main.Send.ModeIndex = 3;
        Check(main.Hub.IsUnavailable && !main.Hub.SendNow.CanExecute(null), "the bridge is not offered on a devnet");
        main.Send.ModeIndex = 0;

        // Send 1.5 to test account C with a payload: confirmation shows the address in one piece and the amount.
        main.Send.Recipient = "Mxf1e0f934a415d37018a318c779441143ba20f8d5";
        main.Send.Amount = "1,5"; main.Send.Payload = "из интерфейса ✓";
        Check(main.Send.IsNewRecipient, "new recipient can be named");
        main.Send.RecipientName = "Тестовый C";
        Shot("send-filled");
        main.Send.SendNow.Execute(null);
        await Until(() => main.Send.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "send included", 90, () => main.Send.Status);
        var confirm = confirmations[^1];
        Check(confirm.Rows.Any(r => r.Value == "Mxf1e0f934a415d37018a318c779441143ba20f8d5"), "confirmation shows the recipient in one piece");
        Check(confirm.Rows.Any(r => r.Label == "Сумма" && r.Value.StartsWith("1.5 MNT (id 0)", StringComparison.Ordinal)), "confirmation shows amount with coin id");
        Check(confirm.Warnings.Any(w => w.StartsWith("Новый получатель", StringComparison.Ordinal)), "new recipient warned");
        Shot("send-done");
        // An address with C's beginning and end but another middle: the substitution warning, in red.
        main.Send.Recipient = "Mxf1e" + new string('f', 34) + "8d5";
        Check(main.Send.RecipientTone == Tone.Error && main.Send.RecipientCheck.StartsWith("Адрес только похож на «Тестовый C» Mxf1e0…f8d5", StringComparison.Ordinal), "lookalike of a contact flagged");
        Shot("send-lookalike");
        main.Send.Recipient = "Mxf1e0f934a415d37018a318c779441143ba20f8d5";
        var dialog = new ConfirmWindow(confirm);
        dialog.Show(); Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        dialog.CaptureRenderedFrame()?.Save(Path.Combine(output, $"{++step:00}-confirm.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
        dialog.Close();
        Check(main.Send.RecipientCheck == "Из адресной книги: Тестовый C" && main.Contacts.Any(c => c.Name == "Тестовый C"), "recipient remembered with its name");

        // Wrong password: nothing is sent.
        answer = "не тот";
        main.Send.Recipient = "Mxf1e0f934a415d37018a318c779441143ba20f8d5"; main.Send.Amount = "1";
        main.Send.SendNow.Execute(null);
        await Until(() => main.Send.Status.StartsWith("Неверный пароль", StringComparison.Ordinal) ? main : null, "wrong password refused", 30, () => main.Send.Status);
        answer = Password;

        // History, decoded by the wallet, loads when the section opens.
        main.Section = Section.History;
        await Until(() => main.History.Items.Any(i => i.Summary.StartsWith("Перевод 1.5 MNT → Mxf1e0", StringComparison.Ordinal)) ? main : null, "send in history", 30, () => main.History.Status);
        Shot("history");
        // The whole history to CSV: a header, a row per transaction, the send to C dated, signed and with its commission.
        main.History.Export.Execute(null);
        await Until(() => main.History.Status.StartsWith("Сохранено", StringComparison.Ordinal) ? main : null, "history saved to CSV", 120, () => main.History.Status);
        var table = savedFile!.Value.Text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Check(savedFile.Value.Name.StartsWith("minter-Mx37980a", StringComparison.Ordinal) && savedFile.Value.Name.EndsWith(".csv", StringComparison.Ordinal), "CSV file name");
        Check(table[0].StartsWith("Дата (UTC),Блок,Хеш,", StringComparison.Ordinal) && table.Length - 1 >= main.History.Items.Count, $"CSV rows: {table.Length - 1} for {main.History.Items.Count} shown");
        Check(table.Any(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\d{4}-\d\d-\d\d \d\d:\d\d:\d\d,\d+,Mt[0-9a-f]+,выполнена,Send,исходящая,Mxf1e0f934a415d37018a318c779441143ba20f8d5,-1\.5,MNT,0\.\d+,MNT,")),
            "the send to C in the CSV with its time, amount and commission");

        // Swap 1 MNT for the devnet token (id 1) along the node's route, 1% slippage.
        // The coin to buy is found by its ticker (the gate's list, or a node's coin_info), and the quote comes by itself.
        main.Section = Section.Swap;
        main.Swap.Sell = main.Swap.Coins.First(c => c.Coin.Id == 0);
        var token = (await shell.Backend!.CoinAsync(1u))!;
        main.Swap.Buy.Query = token.Symbol;
        main.Swap.Buy.Selected = await Until(() => main.Swap.Buy.Results.FirstOrDefault(c => c.Id == 1), "token found by its ticker", 30, () => main.Swap.Buy.EmptyText);
        main.Swap.Amount = "1"; main.Swap.SlippageIndex = 1;
        Check(main.Swap.Slippage == "1", "slippage preset");
        await Until(() => main.Swap.HasQuote ? main : null, "swap quote without pressing anything", 30, () => main.Swap.Status);
        Shot("swap-quote");
        main.Swap.SwapNow.Execute(null);
        await Until(() => main.Swap.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "swap included", 90, () => main.Swap.Status);
        Check(confirmations[^1].Rows.Any(r => r.Label == "Гарантия" && r.Value.StartsWith("не меньше", StringComparison.Ordinal)), "swap confirmation shows the guaranteed minimum");
        await Until(() => main.Balances.Any(b => b.Coin.Id == 1) ? main : null, "token balance after swap");

        // Liquidity: 1 MNT with the token into their pool, the position seen, half of it taken out.
        main.TradeModeIndex = 2;
        main.Liquidity.CoinA = main.Liquidity.Coins.First(c => c.Coin.Id == 0);
        main.Liquidity.CoinB.Query = token.Symbol;
        main.Liquidity.CoinB.Selected = await Until(() => main.Liquidity.CoinB.Results.FirstOrDefault(c => c.Id == 1), "token for the pool", 30, () => main.Liquidity.CoinB.EmptyText);
        main.Liquidity.AmountA = "1";
        await Until(() => main.Liquidity.NeedText.StartsWith("Вместе", StringComparison.Ordinal) ? main : null, "the pool's second amount", 30, () => main.Liquidity.NeedText);
        main.Liquidity.Add.Execute(null);
        await Until(() => main.Liquidity.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "liquidity added", 90, () => main.Liquidity.Status);
        await Until(() => main.Liquidity.HasPositions ? main : null, "position listed", 30, () => main.Liquidity.Status);
        var before = main.Liquidity.Positions[0].Liquidity;
        Shot("liquidity");
        main.Liquidity.Withdraw.Execute("50");
        await Until(() => main.Liquidity.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "half withdrawn", 90, () => main.Liquidity.Status);
        await Until(() => main.Liquidity.Positions.FirstOrDefault()?.Liquidity == before - before / 2 ? main : null, "position halved", 30, () => main.Liquidity.Status);

        // A limit order: sell 1 token for MNT above the pool's price, listed among the account's orders, then cancelled.
        main.TradeModeIndex = 1;
        main.Orders.Sell = main.Orders.Coins.First(c => c.Coin.Id == 1);
        main.Orders.Buy.Query = main.Symbol(0);
        main.Orders.Buy.Selected = await Until(() => main.Orders.Buy.Results.FirstOrDefault(c => c.Id == 0), "base coin to buy", 30, () => main.Orders.Buy.EmptyText);
        main.Orders.AmountSell = "1";
        await Until(() => main.Orders.Range.StartsWith("Пул сейчас", StringComparison.Ordinal) && main.Orders.AmountBuy.Length > 0 ? main : null, "the allowed range", 30, () => main.Orders.Range);
        main.Orders.Place.Execute(null);
        await Until(() => main.Orders.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "order placed", 90, () => main.Orders.Status);
        await Until(() => main.Orders.HasMine ? main : null, "order among mine", 30, () => main.Orders.Status);
        Shot("orders");
        var placed = main.Orders.Mine[0].Order.Id;
        main.Orders.Cancel.Execute(main.Orders.Mine[0]);
        await Until(() => main.Orders.Status.StartsWith("Готово", StringComparison.Ordinal) && main.Orders.Mine.All(o => o.Order.Id != placed) ? main : null, "order cancelled", 90, () => main.Orders.Status);
        main.TradeModeIndex = 0;

        // Sell all of the token back for MNT: a SellAll transaction, the commission paid in the token itself.
        main.Swap.Sell = main.Swap.Coins.First(c => c.Coin.Id == 1);
        main.Swap.Buy.Query = main.Symbol(0);
        main.Swap.Buy.Selected = await Until(() => main.Swap.Buy.Results.FirstOrDefault(c => c.Id == 0), "base coin for selling all", 30, () => main.Swap.Buy.EmptyText);
        main.Swap.All.Execute(null);
        Check(main.Swap.Amount == Units.FormatCoins(main.Swap.Sell!.Value), "the whole token balance as the amount");
        await Until(() => main.Swap.HasQuote ? main : null, "sell-all quote", 30, () => main.Swap.Status);
        Check(main.Swap.QuoteRoute.EndsWith("· продаётся всё", StringComparison.Ordinal), "the quote says everything is sold");
        // Balances read again rebuild the coin list under the selection: "Всё" stays.
        main.Swap.BalancesChanged();
        await Until(() => main.Swap.HasQuote && main.Swap.QuoteRoute.EndsWith("· продаётся всё", StringComparison.Ordinal) ? main : null, "selling all kept after the balances", 30, () => main.Swap.Status);
        Shot("swap-all");
        // A balance read again re-quotes (the cancelled order's refund may land now): press when the button is on.
        await Until(() => main.Swap.SwapNow.CanExecute(null) ? main : null, "sell-all ready", 30, () => main.Swap.Status);
        main.Swap.SwapNow.Execute(null);
        await Until(() => main.Swap.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "sell-all included", 90, () => main.Swap.Status);
        Check(confirmations[^1].Rows.Any(r => r.Label == "Продаёте" && r.Value.StartsWith("всё:", StringComparison.Ordinal)), "the confirmation says everything is sold");
        await Until(() => main.Balances.All(b => b.Coin.Id != 1) ? main : null, "no token left after selling all", 30, () => main.Status);

        // Delegate 2 MNT to the devnet validator.
        main.Section = Section.Staking;
        await Until(() => main.Staking.Candidates.Count > 0 ? main : null, "candidates", 30, () => main.Staking.Status);
        main.Staking.Candidate = main.Staking.Candidates[0];
        main.Staking.Coin = main.Staking.Coins.First(c => c.Coin.Id == 0);
        main.Staking.Amount = "2";
        main.Staking.DelegateNow.Execute(null);
        await Until(() => main.Staking.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "delegation included", 90, () => main.Staking.Status);
        Shot("staking");

        main.Section = Section.Send; main.Send.Multiple = true;
        main.Multisend.Lines = "Mxf1e0f934a415d37018a318c779441143ba20f8d5 0.1\nMx26a08b83ff690136b336c050e96d50178edd62de 0.2 MNT";
        Check(main.Multisend.SummaryTone == Tone.Success && main.Multisend.Summary.StartsWith("Получателей: 2", StringComparison.Ordinal), "multisend lines read while typing");
        Shot("multisend");
        main.Multisend.SendNow.Execute(null);
        await Until(() => main.Multisend.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "multisend included", 90, () => main.Multisend.Status);
        // A check of 1 MNT from B, redeemed by a new account of the same wallet: a wrong password first, then the right
        // one; the issuer pays the commission, so the new account gets exactly 1 MNT.
        main.Section = Section.Send; main.Send.ModeIndex = 2;
        main.IssueCheck.Coin = main.IssueCheck.Coins.First(c => c.Coin.Id == 0);
        main.IssueCheck.Amount = "1"; main.IssueCheck.Password = "пароль-чека"; main.IssueCheck.Days = "7";
        main.IssueCheck.Issue.Execute(null);
        await Until(() => main.IssueCheck.HasIssued ? main : null, "check issued", 30, () => main.IssueCheck.Status);
        var issued = main.IssueCheck.Issued;
        Check(issued.StartsWith("Mc", StringComparison.Ordinal) && confirmations[^1].Action == "Выписать чек", "check text and its confirmation");
        Shot("check-issued");
        main.AccountsPage.AddIndex = (int)AddKind.NewPhrase; main.AccountsPage.Name = "Получатель чека";
        main.AccountsPage.Add.Execute(null);
        Check(main.AccountName == "Получатель чека", "new account selected");
        main.Section = Section.Overview; main.ShowReceive = true;
        main.RedeemCheck.Text = issued;
        Check(main.RedeemCheck.InfoTone == Tone.Success && main.RedeemCheck.Info.StartsWith("Чек на 1 MNT", StringComparison.Ordinal), "check read while pasted");
        main.RedeemCheck.Password = "не тот";
        main.RedeemCheck.Redeem.Execute(null);
        await Until(() => main.RedeemCheck.Status == "Пароль чека не подходит." ? main : null, "wrong check password refused", 30, () => main.RedeemCheck.Status);
        main.RedeemCheck.Password = "пароль-чека";
        main.RedeemCheck.Redeem.Execute(null);
        await Until(() => main.RedeemCheck.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "check redeemed", 90, () => main.RedeemCheck.Status);
        await Until(() => main.Balances.Any(b => b.Coin.Id == 0 && b.Value == Units.PipPerCoin) ? main : null, "exactly 1 MNT on the new account");
        Shot("check-redeemed");
        main.ShowReceive = false;
        main.Selected = main.Accounts.First(x => x.Name == "Тестовый B");
        await Until(() => main.Balances.Count > 1 ? main : null, "back to B");

        // Tokens: a new one (a random 7-letter ticker, the cheapest kind) with its price shown before creating; minted,
        // burned, re-created with a closed supply, put into a pool with MNT, then given to C.
        main.Section = Section.Tokens;
        main.Tokens.ModeIndex = 1;
        var ticker = "UI" + string.Concat(Enumerable.Range(0, 5).Select(_ => (char)('A' + Random.Shared.Next(26))));
        main.Tokens.Symbol = ticker.ToLowerInvariant(); main.Tokens.Name = "Проверка окна"; main.Tokens.Initial = "1000"; main.Tokens.MaxSupply = "10000";
        Check(main.Tokens.Symbol == ticker, "the ticker in capitals");
        await Until(() => main.Tokens.Cost.StartsWith("Обойдётся в", StringComparison.Ordinal) ? main : null, "the token's price", 30, () => main.Tokens.Cost);
        Shot("tokens-create");
        main.Tokens.Create.Execute(null);
        await Until(() => main.Tokens.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "token created", 90, () => main.Tokens.Status);
        Check(confirmations[^1].Rows.Any(r => r.Label == "Тикер" && r.Value == ticker), "the confirmation names the ticker");
        await Until(() => main.Tokens.Selected?.Symbol == ticker ? main : null, "the new token among mine", 30, () => main.Tokens.Status);
        Check(main.Tokens.Selected!.Info.Volume == Units.ParseCoins("1000") && main.Tokens.Selected.Info.MaxSupply == Units.ParseCoins("10000")
            && main.Tokens.Selected.Info.Owner == main.Selected!.Account.Address, "the token as created");
        main.Tokens.ModeIndex = 0;
        main.Tokens.MintAmount = "500";
        main.Tokens.Mint.Execute(null);
        await Until(() => main.Tokens.Status.StartsWith("Готово", StringComparison.Ordinal) && main.Tokens.Selected?.Info.Volume == Units.ParseCoins("1500") ? main : null, "minted", 90, () => main.Tokens.Status);
        Shot("tokens-mine");
        main.Tokens.BurnAmount = "200";
        main.Tokens.Burn.Execute(null);
        await Until(() => main.Tokens.Status.StartsWith("Готово", StringComparison.Ordinal) && main.Tokens.Selected?.Info.Volume == Units.ParseCoins("1300") ? main : null, "burned", 90, () => main.Tokens.Status);
        main.Tokens.StartRecreate.Execute(null);
        Check(main.Tokens.IsCreate && main.Tokens.Recreating && main.Tokens.Symbol == ticker, "re-creating the same ticker");
        main.Tokens.Initial = "50"; main.Tokens.Mintable = false;
        main.Tokens.Create.Execute(null);
        await Until(() => main.Tokens.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "token re-created", 90, () => main.Tokens.Status);
        await Until(() => main.Tokens.Selected is { Info.Mintable: false } t && t.Symbol == ticker && t.Info.Volume == Units.ParseCoins("50") ? main : null, "the re-created token", 30, () => main.Tokens.Status);
        var token2 = main.Tokens.Selected!.Info.Id;
        // A pool of 10 of it and 1 MNT: the starting price 0.1 MNT.
        main.Tokens.ModeIndex = 2;
        await Until(() => main.Tokens.Coins.Any(c => c.Coin.Id == token2) ? main : null, "the new token among the balances", 30, () => main.Status);
        main.Tokens.CoinA = main.Tokens.Coins.First(c => c.Coin.Id == token2);
        main.Tokens.CoinB = main.Tokens.Coins.First(c => c.Coin.Id == 0);
        main.Tokens.AmountA = "10"; main.Tokens.AmountB = "1";
        Check(main.Tokens.PoolText.StartsWith($"Начальная цена: 1 {ticker} = 0.1 MNT", StringComparison.Ordinal), $"the pool's starting price: {main.Tokens.PoolText}");
        Shot("tokens-pool");
        main.Tokens.CreatePool.Execute(null);
        await Until(() => main.Tokens.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "pool created", 90, () => main.Tokens.Status);
        Check(await shell.Backend!.SwapPoolAsync(token2, 0) is { } pool && pool.Amount0 + pool.Amount1 > 0, "the pool exists");
        // The token to C: gone from mine, C its owner.
        main.Tokens.ModeIndex = 0;
        main.Tokens.Selected = main.Tokens.Mine.First(t => t.Info.Id == token2);
        main.Tokens.NewOwner = "Mxf1e0f934a415d37018a318c779441143ba20f8d5";
        main.Tokens.GiveAway.Execute(null);
        await Until(() => main.Tokens.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "ownership given", 90, () => main.Tokens.Status);
        Check(main.Tokens.Mine.All(t => t.Info.Id != token2) && (await shell.Backend!.CoinAsync(token2))?.Owner == Address.Parse("Mxf1e0f934a415d37018a318c779441143ba20f8d5"), "C owns the token");

        // A shared account of B and the check's recipient, 2 of 2: created by B, funded with 5 MNT, 1 MNT sent from it
        // to C — proposed and signed by B, the text signed by the other member, then sent.
        var b = main.Selected!.Account.Address;
        var other = main.Accounts.First(x => x.Name == "Получатель чека").Account.Address;
        main.Section = Section.Multisig;
        main.Multisig.ModeIndex = 0;
        Check(main.Multisig.MemberRows is [{ } first, { Address: "" }] && first.Address == b.ToString(), "this account first among the members");
        main.Multisig.Pick = main.Multisig.Known.First(k => k.Address == other);
        Check(main.Multisig.MemberRows[1].Address == other.ToString() && main.Multisig.MemberRows[1].HintTone == Tone.Success, "the other member picked from the accounts");
        main.Multisig.SetThreshold.Execute("all");
        Check(main.Multisig.ThresholdValue == 2 && main.Multisig.ThresholdText == "Тратить можно, только когда подпишут все 2.", $"threshold in words: {main.Multisig.ThresholdText}");
        main.Multisig.Create.Execute(null);
        await Until(() => main.Multisig.HasCreated ? main : null, "shared account created", 90, () => main.Multisig.Status);
        var shared = Address.Parse(main.Multisig.Created);
        Check((await shell.Backend!.AccountAsync(shared)).Multisig is { Threshold: 2, Members.Count: 2 }, "the shared account's members on the chain");
        Shot("multisig-created");
        main.Section = Section.Send;
        main.Send.Recipient = shared.ToString(); main.Send.Coin = main.Send.Coins.First(c => c.Coin.Id == 0); main.Send.Amount = "5"; main.Send.Payload = "";
        main.Send.SendNow.Execute(null);
        await Until(() => main.Send.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "shared account funded", 90, () => main.Send.Status);
        main.Section = Section.Multisig;
        main.Multisig.ModeIndex = 1;
        main.Multisig.LoadAccount.Execute(null);
        await Until(() => main.Multisig.AccountInfo.StartsWith("Порог 2 из 2", StringComparison.Ordinal) && main.Multisig.Coins.Count > 0 ? main : null, "the shared account read", 30, () => main.Multisig.Status);
        main.Multisig.Recipient = "Mxf1e0f934a415d37018a318c779441143ba20f8d5"; main.Multisig.Amount = "1";
        main.Multisig.Propose.Execute(null);
        await Until(() => main.Multisig.HasTx && main.Multisig.Progress == "Подписано: вес 1 из 2 нужных." ? main : null, "proposed and signed by B", 30, () => main.Multisig.Status + " " + main.Multisig.TextInfo);
        Check(!main.Multisig.CanSend && !main.Multisig.CanSign && confirmations[^1].Action == "Подписать", "one signature is not enough; B cannot sign twice");
        var text = main.Multisig.Text;
        Shot("multisig-signed-once");
        main.Selected = main.Accounts.First(x => x.Name == "Получатель чека");
        main.Multisig.Text = ""; main.Multisig.Text = text;   // as the other member pastes it
        await Until(() => main.Multisig.CanSign ? main : null, "the other member can sign", 30, () => main.Multisig.TextInfo);
        main.Multisig.Sign.Execute(null);
        await Until(() => main.Multisig.CanSend ? main : null, "signed by both", 30, () => main.Multisig.Status + " " + main.Multisig.Progress);
        var sharedBefore = (await shell.Backend!.AccountAsync(shared)).BalanceOf(0);
        main.Multisig.SendNow.Execute(null);
        await Until(() => main.Multisig.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "sent from the shared account", 90, () => main.Multisig.Status);
        Check((await shell.Backend!.AccountAsync(shared)).BalanceOf(0) <= sharedBefore - Units.PipPerCoin, "1 MNT left the shared account");
        Shot("multisig-sent");
        main.Selected = main.Accounts.First(x => x.Name == "Тестовый B");
        await Until(() => main.Balances.Count > 1 ? main : null, "back to B after the shared account");

        // Own validator: a new candidate (a random key) declared with 1 MNT, switched on and off, its control address
        // given to C; then 1 MNT of B's stake moved to it from the devnet validator, B's stake seen there.
        main.Section = Section.Staking;
        main.Staking.ModeIndex = 1;
        await Until(() => main.Validator.DeclareReward.Length > 0 ? main : null, "own candidates read", 30, () => main.Validator.Status);
        var nodeKey = "Mp" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        main.Validator.DeclareKey = nodeKey; main.Validator.DeclareCommission = "7"; main.Validator.DeclareStake = "1";
        main.Validator.Declare.Execute(null);
        await Until(() => main.Validator.Status.StartsWith("Готово", StringComparison.Ordinal) && main.Validator.Mine.Any(c => c.Key == nodeKey) ? main : null, "candidate declared", 90, () => main.Validator.Status);
        main.Validator.Selected = main.Validator.Mine.First(c => c.Key == nodeKey);
        main.Validator.On.Execute(null);
        await Until(() => main.Validator.Status.StartsWith("Готово", StringComparison.Ordinal) && main.Validator.Selected?.Info.Status == 2 ? main : null, "candidate on", 90, () => main.Validator.Status);
        Shot("validator");
        main.Validator.Off.Execute(null);
        await Until(() => main.Validator.Status.StartsWith("Готово", StringComparison.Ordinal) && main.Validator.Selected?.Info.Status == 1 ? main : null, "candidate off", 90, () => main.Validator.Status);
        main.Validator.Control = "Mxf1e0f934a415d37018a318c779441143ba20f8d5";
        main.Validator.SaveAddresses.Execute(null);
        await Until(() => main.Validator.Status.StartsWith("Готово", StringComparison.Ordinal) && main.Validator.Selected?.Info.Control == Address.Parse("Mxf1e0f934a415d37018a318c779441143ba20f8d5") ? main : null, "control address changed", 90, () => main.Validator.Status);
        main.Staking.ModeIndex = 0;
        main.Staking.Load.Execute(null);
        await Until(() => main.Staking.Candidates.Any(c => c.Key == nodeKey) ? main : null, "the new candidate listed", 30, () => main.Staking.Status);
        main.Staking.Candidate = main.Staking.Candidates.First(c => c.Info.Validator);
        await Until(() => main.Staking.MyStake.StartsWith("Ваш стейк здесь:", StringComparison.Ordinal) ? main : null, "B's stake with the devnet validator", 30, () => main.Staking.MyStake);
        main.Staking.MoveTarget = main.Staking.Candidates.First(c => c.Key == nodeKey);
        main.Staking.Coin = main.Staking.Coins.First(c => c.Coin.Id == 0); main.Staking.Amount = "1";
        main.Staking.Move.Execute(null);
        await Until(() => main.Staking.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "stake moved", 90, () => main.Staking.Status);
        Check(confirmations[^1].Rows.Any(r => r.Label == "Куда" && r.Value.Contains(nodeKey, StringComparison.Ordinal)), "the move's confirmation names the new candidate");
        Shot("stake-moved");

        main.Section = Section.Settings;
        Check(main.Settings.Sources.Count == 2 && main.Settings.Sources[0].Source.IsDefaultGate && main.Settings.SourceIndex == 1 && main.Settings.CanRemove,
            "the project's gate offered first, the devnet still in use");
        Shot("settings");

        // «Вид»: addresses in groups of four and every decimal, then back.
        main.Settings.AddressIndex = 1; main.Settings.DecimalsIndex = 1;
        Dispatcher.UIThread.RunJobs();
        Check(Display.GroupAddresses && Display.AllDecimals && main.AddressText == "Mx 3798 0a12 b1be 0855 384b 8d4f 24b2 d795 7f68 0149", "addresses in groups after the switch");
        var saved = WalletSettings.Load(data);
        Check(saved.GroupAddresses && saved.AllDecimals, "the «Вид» switches saved");
        main.Section = Section.Overview;
        await Until(() => main.Balances.Count > 1 ? main : null, "balances read again");
        Check(Marked(main.AddressText) is ["Mx", " 3798", "0149"], "grouped address with its prefix and edges highlighted");
        Shot("overview-grouped");
        main.Settings.AddressIndex = 0; main.Settings.DecimalsIndex = 0;
        Dispatcher.UIThread.RunJobs();
        Check(!Display.GroupAddresses && !Display.AllDecimals && main.AddressText == "Mx37980a12b1be0855384b8d4f24b2d7957f680149", "back to the address in one piece");
        await Until(() => main.Balances.Count > 1 ? main : null, "balances read again");

        // The tray: a minimized window hides into it and comes back; the close button hides it too.
        var tray = new TrayHost(Application.Current!, new Avalonia.Controls.ApplicationLifetimes.ClassicDesktopStyleApplicationLifetime(), window!, shell);
        main.Settings.MinimizeToTray = true; main.Settings.CloseToTray = true;
        window!.WindowState = WindowState.Minimized; Dispatcher.UIThread.RunJobs();
        Check(!window.IsVisible, "a minimized window hides into the tray");
        tray.Show(); Dispatcher.UIThread.RunJobs();
        Check(window.IsVisible && window.WindowState == WindowState.Normal, "the tray brings the window back");
        window.Close(); Dispatcher.UIThread.RunJobs();
        Check(!window.IsVisible, "the close button hides the window into the tray");
        tray.Show(); Dispatcher.UIThread.RunJobs();
        Check(window.IsVisible, "and it comes back from there");
        // A second launch on the same data directory shows the running wallet instead of starting another.
        var instanceDir = Directory.CreateTempSubdirectory("minterwallet-instance-").FullName;
        var firstInstance = SingleInstance.Claim(instanceDir)!;
        var shown = false;
        firstInstance.Listen(() => shown = true);
        SingleInstance? secondInstance = null;
        var launch = new Thread(() => secondInstance = SingleInstance.Claim(instanceDir));
        launch.Start();
        while (launch.IsAlive) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(50); }
        await Until(() => shown ? main : null, "the running wallet asked to show itself", 10);
        Check(secondInstance == null, "no second wallet on one data directory");

        // The light theme, then lock and unlock with the password.
        main.Section = Section.Overview;
        main.Settings.ThemeIndex = 2; Shot("overview-light");
        main.Section = Section.Send; main.Send.Multiple = false; Shot("send-light");
        main.Settings.ThemeIndex = 1;
        const string NewPassword = "ui-check-новый-пароль";
        main.Settings.CurrentPassword = Password; main.Settings.NewPassword = NewPassword; main.Settings.RepeatPassword = NewPassword;
        main.Settings.ChangePassword.Execute(null);
        await Until(() => main.Settings.Status.StartsWith("Пароль изменён", StringComparison.Ordinal) ? main : null, "password changed", 30, () => main.Settings.Status);
        main.LockNow.Execute(null);
        var unlock = (UnlockViewModel)shell.Page;
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
        Check(window!.FocusManager?.GetFocusedElement() is TextBox { PasswordChar: '•' }, "the password field has the focus on the lock screen");
        Shot("locked");
        unlock.Password = Password;
        unlock.Unlock.Execute(null);
        await Until(() => unlock.Tone == Tone.Error ? unlock : null, "the old password no longer opens the wallet");
        unlock.Password = NewPassword;
        unlock.Unlock.Execute(null);
        await Until(() => shell.Page as MainViewModel, "unlocked");
    }

    private static void ScrollToEnd()
    {
        Dispatcher.UIThread.RunJobs();
        foreach (var scroll in window!.GetVisualDescendants().OfType<ScrollViewer>().Where(s => s.IsEffectivelyVisible)) scroll.ScrollToEnd();
    }

    private static void Shot(string name)
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        try { window?.CaptureRenderedFrame()?.Save(Path.Combine(output, $"{++step:00}-{name}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        catch (ObjectDisposedException) { }   // the window is already closed
    }

    private static void Check(bool condition, string what) { if (!condition) throw new InvalidOperationException(what); }

    // What a TextBlock shows: its runs when it has them (AddressMarks), its Text otherwise.
    private static string? Shown(TextBlock block) => block.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Run>().Select(r => r.Text)) : block.Text;

    // The highlighted parts of the shown text block that holds this address.
    private static string[] Marked(string address)
    {
        Dispatcher.UIThread.RunJobs();
        var block = window!.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(b => b.IsEffectivelyVisible && AddressMarks.GetText(b) == address);
        return block?.Inlines?.OfType<Run>().Where(r => r.FontWeight == Avalonia.Media.FontWeight.SemiBold).Select(r => r.Text ?? "").ToArray() ?? [];
    }

    // Amounts and addresses as the views show them.
    private static void Formatting()
    {
        static string Plain(string text) => new(text.Where(c => !char.IsWhiteSpace(c)).ToArray());
        static System.Numerics.BigInteger Pip(string coins) => Units.ParseCoins(coins);
        var cases = new (string Coins, string Shown)[]
        {
            ("1234567.89123", "1234567.8912"), ("1.2345678", "1.2345"), ("1.5", "1.5"), ("0.1234", "0.1234"),
            ("0.000001234", "0.000001234"), ("0.0000123456789", "0.00001234"), ("0.000000000000000001", "0.000000000000000001"), ("0", "0"),
        };
        foreach (var (coins, shown) in cases) Check(Plain(Display.Amount(Pip(coins))) == shown, $"{coins} shown as {shown}, not {Display.Amount(Pip(coins))}");
        Check(Plain(Display.Amount(-Pip("0.00005"))) == "−0.00005", "negative small amount");
        Check(Display.Usd(Pip("0.4123")) == "$0.412" && Display.Usd(Pip("0.000412")) == "$0.000412" && Plain(Display.Usd(Pip("12.40"))) == "$12.4",
            $"dollars: {Display.Usd(Pip("0.4123"))}, {Display.Usd(Pip("0.000412"))}, {Display.Usd(Pip("12.40"))}");
        var address = Address.Parse("Mx37980a12b1be0855384b8d4f24b2d7957f680149");
        Check(Display.Address(address) == "Mx37980a12b1be0855384b8d4f24b2d7957f680149", "address in one piece by default");
        Display.Use(new WalletSettings { AllDecimals = true, GroupAddresses = true });
        Check(Plain(Display.Amount(Pip("1.2345678"))) == "1.2345678" && Display.Address(address) == "Mx 3798 0a12 b1be 0855 384b 8d4f 24b2 d795 7f68 0149"
            && Display.Usd(Pip("0.4123")) == "$0.412", "every decimal and grouped addresses when asked; dollars stay short");
        Display.Use(new WalletSettings());
    }

    private static async Task<T> Until<T>(Func<T?> probe, string what, int seconds = 30, Func<string>? detail = null) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (probe() is { } value) return value;
            await Task.Delay(100);
        }
        throw new TimeoutException($"{what}: not reached in {seconds} s{(detail != null ? $" ({detail()})" : "")}");
    }
}
