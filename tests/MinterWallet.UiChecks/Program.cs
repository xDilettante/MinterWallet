using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
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
            await FirstRun(url, first);
            await Operations(url, data);
            await English(url, Directory.CreateTempSubdirectory("minterwallet-ui-").FullName);
            Console.WriteLine($"PASS ui: {step} screens rendered to {output}; create (copy, skip and paste-check), forgot password, restore from a pasted numbered phrase, " +
                "phrase reveal and copy, next address, new phrase, watch-only, rename, remove; import, balances, send, wrong password, history, swap by ticker with automatic quote, " +
                "every screen in English with no Cyrillic left, the language switched in place, " +
                "liquidity added and halved, limit order placed and cancelled, delegation, multisend, check issued and redeemed, sources, receive with QR, address book, password change, lock/unlock against the devnet; dark and light themes");
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
            if (block.IsEffectivelyVisible && block.Text is { } text && Localization.HasCyrillic(text) && text != "Русский"
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
        Scan("send"); Shot("en-send");
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
        main.Section = Section.Accounts; Scan("accounts");
        main.Section = Section.Settings; Scan("settings"); Shot("en-settings");
        var dialog = new ConfirmWindow(new ConfirmRequest("Перевод",
            [("Операция", "Перевод 1.5 MNT → Mxf1e0…f8d5"), ("Получатель", "Mx f1e0 f934 a415 d370 18a3 18c7 7944 1143 ba20 f8d5"), ("Комиссия", "0.068 MNT (id 0), gas price 1"), ("Сеть", "minter-devnet (chain ID 2) · devnet")],
            ["Новый получатель. У адресов Minter нет контрольной суммы: опечатка или подмена в буфере обмена дадут другой действительный адрес. Сверьте все группы символов."]));
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
        Check(main.IsWatchOnly && main.Accounts.Count == 4, "watch-only account added");
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
        await Until(() => main.Accounts.Count == 3 ? main : null, "account removed", 30, () => accounts.Status);
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
        var shell = new Shell(data) { Confirm = request => { confirmations.Add(request); return Task.FromResult<string?>(answer); } };
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
        main.ToggleReceive.Execute(null);
        Check(main.ShowReceive, "receive panel");
        Shot("receive");
        main.ToggleReceive.Execute(null);

        // Send 1.5 to test account C with a payload: confirmation shows the grouped address and the amount.
        main.Section = Section.Send;
        main.Send.Recipient = "Mxf1e0f934a415d37018a318c779441143ba20f8d5";
        main.Send.Amount = "1,5"; main.Send.Payload = "из интерфейса ✓";
        Check(main.Send.IsNewRecipient, "new recipient can be named");
        main.Send.RecipientName = "Тестовый C";
        Shot("send-filled");
        main.Send.SendNow.Execute(null);
        await Until(() => main.Send.Status.StartsWith("Готово", StringComparison.Ordinal) ? main : null, "send included", 90, () => main.Send.Status);
        var confirm = confirmations[^1];
        Check(confirm.Rows.Any(r => r.Value == "Mx f1e0 f934 a415 d370 18a3 18c7 7944 1143 ba20 f8d5"), "confirmation shows the grouped recipient");
        Check(confirm.Rows.Any(r => r.Label == "Сумма" && r.Value.StartsWith("1.5 MNT (id 0)", StringComparison.Ordinal)), "confirmation shows amount with coin id");
        Check(confirm.Warnings.Any(w => w.StartsWith("Новый получатель", StringComparison.Ordinal)), "new recipient warned");
        Shot("send-done");
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

        main.Section = Section.Settings;
        Check(main.Settings.Sources.Count == 2 && main.Settings.Sources[0].Source.IsDefaultGate && main.Settings.SourceIndex == 1 && main.Settings.CanRemove,
            "the project's gate offered first, the devnet still in use");
        Shot("settings");

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
