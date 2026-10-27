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
if (args is not ["--devnet", var url]) { Console.WriteLine("Usage: MinterWallet.UiChecks --devnet http://WSL-IP:38843"); return 2; }
var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "ui-checks"));
Directory.CreateDirectory(output);
var session = HeadlessUnitTestSession.StartNew(typeof(UiChecksApp));
var code = await session.Dispatch(() => UiScenario.Run(url, output), CancellationToken.None);
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
            Console.WriteLine($"PASS ui: {step} screens rendered to {output}; create (copy, skip and paste-check), forgot password, restore from a pasted numbered phrase, " +
                "phrase reveal and copy, next address, new phrase, watch-only, rename, remove; import, balances, send, wrong password, history, swap by ticker with automatic quote, " +
                "delegation, multisend, sources, lock/unlock against the devnet; dark and light themes");
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

    private static void Settings(string directory, string url, string theme) =>
        File.WriteAllText(Path.Combine(directory, WalletSettings.FileName), $$"""{"Sources":[{"Name":"devnet (WSL)","Url":"{{url}}"}],"SelectedSource":0,"GasPriceCap":10,"AutoLockMinutes":10,"Theme":"{{theme}}"}""");

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
        accounts.Remove.Execute(null);
        Check(accounts.ConfirmingRemove, "removal asks first");
        accounts.Remove.Execute(null);
        await Until(() => main.Accounts.Count == 3 ? main : null, "account removed", 30, () => accounts.Status);
        window.Close();
    }

    private static async Task Operations(string url, string data)
    {
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

        // Send 1.5 to test account C with a payload: confirmation shows the grouped address and the amount.
        main.Section = Section.Send;
        main.Send.Recipient = "Mxf1e0f934a415d37018a318c779441143ba20f8d5";
        main.Send.Amount = "1,5"; main.Send.Payload = "из интерфейса ✓";
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
        Check(main.Send.RecipientCheck.StartsWith("Вы уже отправляли", StringComparison.Ordinal), "recipient remembered");

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
        main.Section = Section.Settings;
        Check(main.Settings.Sources.Count == 2 && main.Settings.Sources[0].Source.IsDefaultGate && main.Settings.SourceIndex == 1 && main.Settings.CanRemove,
            "the project's gate offered first, the devnet still in use");
        Shot("settings");

        // The light theme, then lock and unlock with the password.
        main.Section = Section.Overview;
        main.Settings.ThemeIndex = 2; Shot("overview-light");
        main.Section = Section.Send; main.Send.Multiple = false; Shot("send-light");
        main.Settings.ThemeIndex = 1;
        main.LockNow.Execute(null);
        var unlock = (UnlockViewModel)shell.Page;
        Shot("locked");
        unlock.Password = Password;
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
        window?.CaptureRenderedFrame()?.Save(Path.Combine(output, $"{++step:00}-{name}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
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
