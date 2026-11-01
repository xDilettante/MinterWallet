using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Minter.Protocol;
using MinterWallet;
using MinterWallet.Core;
using MinterWallet.Mobile;

Environment.SetEnvironmentVariable("MINTERWALLET_USAGE_URL", "off");
var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "mobile-checks"));
Directory.CreateDirectory(output);
var session = HeadlessUnitTestSession.StartNew(typeof(MobileChecksApp));
var url = args is ["--devnet" or "--mainnet", var u] ? u : null;
var code = await session.Dispatch(() => MobileScenario.RunAsync(output, url), CancellationToken.None);
Environment.Exit(code);
return code;

public static class MobileChecksApp
{
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}

internal sealed class FakeMobilePlatform : IMobilePlatform
{
    public List<(string Title, string Text)> Notifications { get; } = [];
    public bool Secure { get; private set; }
    public void Notify(string title, string text) => Notifications.Add((title, text));
    public void SetSecure(bool secure) => Secure = secure;
}

public static class MobileScenario
{
    private const double PhoneWidth = 412;
    private const double PhoneHeight = 915;
    private const string Password = "mobile-check-пароль";
    private static Window? window;
    private static MobileRoot? root;
    private static string output = "";
    private static int step;
    private static readonly HashSet<string> Untranslated = [];
    private static readonly HashSet<string> Overflows = [];

    public static async Task<int> RunAsync(string outputDirectory, string? liveUrl)
    {
        output = outputDirectory;
        foreach (var old in Directory.GetFiles(output, "*.png")) File.Delete(old);
        var data = Directory.CreateTempSubdirectory("minterwallet-mobile-").FullName;
        var enData = Directory.CreateTempSubdirectory("minterwallet-mobile-en-").FullName;
        var platform = new FakeMobilePlatform();
        MobileHost.Platform = platform;
        try
        {
            await OfflineFlowAsync(data, platform, liveUrl);
            await EnglishFlowAsync(enData);
            if (Overflows.Count > 0)
                throw new InvalidOperationException("Horizontal overflow on 412px phone screen:\n" + string.Join("\n", Overflows));
            if (Untranslated.Count > 0)
                throw new InvalidOperationException("Untranslated Cyrillic in English mode:\n" + string.Join("\n", Untranslated));
            Console.WriteLine($"PASS mobile: {step} phone screens (412×915) rendered to {output}; zero horizontal overflow, zero untranslated Cyrillic, " +
                "onboarding, 2-column phrase grid, unlock, bottom nav, «Ещё» sheet, confirm bottom sheet, back gesture, FLAG_SECURE and notifications verified.");
            return 0;
        }
        catch (Exception e)
        {
            Shot("failure");
            Console.WriteLine("FAIL mobile: " + e);
            return 1;
        }
        finally
        {
            root?.Shell.StopTimers();
            window?.Close();
            foreach (var dir in new[] { data, enData }) try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

    private static async Task OfflineFlowAsync(string data, FakeMobilePlatform platform, string? liveUrl)
    {
        new WalletSettings
        {
            Sources = [new SourceSettings { Name = "MinterGate", Url = liveUrl ?? "http://127.0.0.1:1" }],
            AutoLockMinutes = 10, Theme = "dark", CheckUpdates = false, Telemetry = false, Language = "ru",
        }.Save(data);

        var shell = new Shell(data) { AutoRefresh = false };
        OpenPhone(shell);

        // 1. Welcome screen
        Check(shell.Page is WelcomeViewModel, "starts on welcome screen");
        Check(!platform.Secure, "welcome screen is not FLAG_SECURE");
        VerifyScreen("welcome");

        // 2. Create wallet: Step 1 (phrase on 2-column grid), Step 2 (check), Step 3 (password)
        ((WelcomeViewModel)shell.Page).Create.Execute(null);
        var create = (CreateWalletViewModel)shell.Page;
        Check(platform.Secure, "create wallet screen turns on FLAG_SECURE");
        VerifyScreen("create-phrase");
        create.Next.Execute(null);
        VerifyScreen("create-check");
        create.Skip.Execute(null);
        VerifyScreen("create-password");

        // Back gesture walks back through create steps to Welcome
        Check(Back(), "back from step 3 to step 2");
        Check(create.IsStep2, "now on step 2");
        Check(Back(), "back from step 2 to step 1");
        Check(create.IsStep1, "now on step 1");
        Check(Back(), "back from step 1 to welcome");
        Check(shell.Page is WelcomeViewModel, "back on welcome");

        // 3. Import screen (mnemonic)
        ((WelcomeViewModel)shell.Page).Restore.Execute(null);
        var import = (ImportViewModel)shell.Page;
        Check(platform.Secure, "import screen turns on FLAG_SECURE");
        import.Secret = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        import.Name = "Основной";
        import.Password = Password;
        import.Repeat = Password;
        VerifyScreen("import");
        import.Finish.Execute(null);

        var main = await UntilAsync(() => shell.Page as MainViewModel, "main screen opened");
        Check(!platform.Secure, "overview clears FLAG_SECURE");

        // Populate sample balances, delegations, frozen funds and history so every card is exercised on the phone width
        PopulateSampleData(main);
        if (liveUrl != null)
            await UntilAsync(() => main.Balances.Count > 0 ? main : null, "live balances", 30);

        // 4. Overview, Derivation expanded + parameters, Receive QR stacked vertically
        VerifyScreen("overview");
        main.Derivation.Toggle.Execute(null);
        VerifyScreen("derivation-cards");
        main.Derivation.Edit.Execute(null);
        VerifyScreen("derivation-params");
        main.Derivation.Edit.Execute(null);
        main.Derivation.Toggle.Execute(null);

        main.ShowReceive = true;
        VerifyScreen("receive-qr");
        main.ShowReceive = false;

        // 5. Send: single, multisend rows, check issue with QR
        main.Section = Section.Send;
        VerifyScreen("send-single");
        main.Send.ModeIndex = 1;
        VerifyScreen("send-multisend");
        main.Send.ModeIndex = 2;
        VerifyScreen("send-check");

        // Back from Send returns to Overview
        Check(Back(), "back from Send returns to Overview");
        Check(main.IsOverview, "returned to Overview");

        // 6. Swap: swap now, limit orders, liquidity
        main.Section = Section.Swap;
        VerifyScreen("swap-now");
        main.TradeModeIndex = 1;
        VerifyScreen("swap-orders");
        main.TradeModeIndex = 2;
        VerifyScreen("swap-liquidity");
        main.TradeModeIndex = 0;

        // 7. History + expanded details
        main.Section = Section.History;
        VerifyScreen("history");

        // 8. «Ещё» sheet
        var mainView = root!.FindDescendantOfType<MobileMainView>()!;
        var moreGrid = mainView.FindControl<Grid>("More")!;
        moreGrid.IsVisible = true;
        VerifyScreen("more-sheet");
        Check(Back(), "back closes the «Ещё» sheet");
        Check(!moreGrid.IsVisible, "«Ещё» sheet closed");

        // 9. Bridge: out and in
        main.Section = Section.Bridge;
        VerifyScreen("bridge-out");
        main.BridgeDirection = 1;
        VerifyScreen("bridge-in");
        main.BridgeDirection = 0;

        // 10. Staking: delegation and validator
        main.Section = Section.Staking;
        VerifyScreen("staking-delegation");
        main.Staking.ModeIndex = 1;
        VerifyScreen("staking-validator");
        main.Staking.ModeIndex = 0;

        // 11. Tokens: mine, create, pool
        main.Section = Section.Tokens;
        VerifyScreen("tokens-mine");
        main.Tokens.ModeIndex = 1;
        VerifyScreen("tokens-create");
        main.Tokens.ModeIndex = 2;
        VerifyScreen("tokens-pool");
        main.Tokens.ModeIndex = 0;

        // 12. Multisig: create and transaction
        main.Section = Section.Multisig;
        VerifyScreen("multisig-create");
        main.Multisig.ModeIndex = 1;
        VerifyScreen("multisig-tx");
        main.Multisig.ModeIndex = 0;

        // 13. Accounts: groups, add tabs, vanity
        main.Section = Section.Accounts;
        Check(platform.Secure, "accounts section turns on FLAG_SECURE");
        VerifyScreen("accounts");
        main.AccountsPage.AddIndex = 4;
        VerifyScreen("accounts-vanity");
        main.AccountsPage.AddIndex = 0;

        // 14. Settings: dark & light themes, tray section hidden
        main.Section = Section.Settings;
        VerifyScreen("settings-dark");
        foreach (var block in root!.GetVisualDescendants().OfType<TextBlock>())
            Check(!(block.IsEffectivelyVisible && block.Text is "Окно и трей" or "Window and tray"), "desktop tray settings are hidden on mobile");
        main.Settings.ThemeIndex = 2; // Light
        VerifyScreen("settings-light");
        main.Settings.ThemeIndex = 1; // Dark

        // 15. Confirmation bottom sheet (ConfirmSheet)
        var sheet = root!.FindControl<ConfirmSheet>("Sheet")!;
        var confirmTask = sheet.AskAsync(new ConfirmRequest(
            "Перевод 125 BIP",
            [("Счёт", "Основной · Mx9858effd232b4033e47d90003d41ec34ecaeda94"), ("Получатель", "Mx37980a12b1be0855384b8d4f24b2d7957f680149"), ("Сумма", "125 BIP"), ("Комиссия", "0.01 BIP")],
            ["Вы ещё не отправляли монеты на этот адрес. Сверьте его по краям."]));
        VerifyScreen("confirm-sheet");
        Check(Back(), "back gesture cancels confirmation sheet");
        Check(await confirmTask == null, "cancelled confirmation returns null");

        // 16. Lock & Unlock screen
        main.LockNow.Execute(null);
        Check(shell.Page is UnlockViewModel, "wallet locked");
        var unlock = (UnlockViewModel)shell.Page;
        unlock.Forgot.Execute(null);
        VerifyScreen("unlock");
    }

    private static async Task EnglishFlowAsync(string data)
    {
        new WalletSettings
        {
            Sources = [new SourceSettings { Name = "MinterGate", Url = "http://127.0.0.1:1" }],
            AutoLockMinutes = 10, Theme = "dark", CheckUpdates = false, Telemetry = false, Language = "en",
        }.Save(data);

        var shell = new Shell(data) { AutoRefresh = false };
        OpenPhone(shell);
        Check(Localization.English, "English localization active");

        ScanEnglish("en-welcome");
        ((WelcomeViewModel)shell.Page).ImportKey.Execute(null);
        var import = (ImportViewModel)shell.Page;
        import.Secret = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("minterwallet-mobile-en")));
        import.Name = "Main";
        import.Password = Password;
        import.Repeat = Password;
        ScanEnglish("en-import");
        import.Finish.Execute(null);

        var main = await UntilAsync(() => shell.Page as MainViewModel, "English main screen");
        PopulateSampleData(main);
        main.ShowReceive = true;
        ScanEnglish("en-overview");
        Shot("en-overview");

        foreach (var section in Enum.GetValues<Section>())
        {
            main.Section = section;
            ScanEnglish($"en-{section}");
        }

        var mainView = root!.FindDescendantOfType<MobileMainView>()!;
        mainView.FindControl<Grid>("More")!.IsVisible = true;
        ScanEnglish("en-more-sheet");
        Shot("en-more-sheet");
        mainView.FindControl<Grid>("More")!.IsVisible = false;

        var sheet = root!.FindControl<ConfirmSheet>("Sheet")!;
        _ = sheet.AskAsync(new ConfirmRequest("Перевод", [("Сумма", "10 BIP")], []));
        ScanEnglish("en-confirm-sheet");
        sheet.Cancel();
    }

    private static void PopulateSampleData(MainViewModel main)
    {
        if (main.Balances.Count == 0)
        {
            var bip = new BalanceItem(new CoinRef(0, "BIP"), Units.ParseCoins("15420.5"), Units.ParseCoins("15420.5"), "BIP", "$18.50", "1 720 ₽");
            var usdt = new BalanceItem(new CoinRef(3494, "USDTBSC"), Units.ParseCoins("245.80"), Units.ParseCoins("204833"), "BIP", "$245.80", "22 860 ₽");
            main.Balances.Add(bip);
            main.Balances.Add(usdt);
            main.Send.Coins.Add(bip); main.Send.Coins.Add(usdt); main.Send.Coin = bip;
            main.Swap.Coins.Add(bip); main.Swap.Coins.Add(usdt); main.Swap.Sell = bip;
            main.Orders.Coins.Add(bip); main.Orders.Sell = bip;
            main.Liquidity.Coins.Add(bip); main.Liquidity.CoinA = bip;
            main.Staking.Coins.Add(bip); main.Staking.Coin = bip;
            main.IssueCheck.Coins.Add(bip); main.IssueCheck.Coin = bip;
            main.Hub.Coins.Add(usdt); main.Hub.Coin = usdt;
        }
        if (main.Delegations.Count == 0)
            main.Delegations.Add(new DelegationItem(new Delegation(new CoinRef(0, "BIP"), Units.ParseCoins("50000"), Units.ParseCoins("50000")), "BIP", "$60", "5 580 ₽"));
        if (main.Frozen.Count == 0)
            main.Frozen.Add(new FrozenItem("1 000 BIP", "1 000 BIP", "Вывод из стейка · блок 40 250 000 (~12 дн.)"));
        if (main.History.Items.Count == 0)
        {
            var sender = Address.Parse("Mx9858effd232b4033e47d90003d41ec34ecaeda94");
            var recipient = Address.Parse("Mx37980a12b1be0855384b8d4f24b2d7957f680149");
            var entry = new HistoryEntry("Mt1234567890abcdef1234567890abcdef1234567890abcdef1234567890abcdef", 40195300, true, "", TxType.Send, sender, Direction.Outgoing,
                "Перевод 125 BIP", "");
            var details = new TxDetails("Перевод", $"→ {History.Short(recipient)}", [new TxMove(-Units.ParseCoins("125"), 0)], "0.01 BIP",
                [("Статус", "выполнена"), ("Отправитель", sender.ToString()), ("Получатель", recipient.ToString()), ("Сумма", "125 BIP"), ("Комиссия", "0.01 BIP"), ("Хеш", entry.Hash)],
                recipient);
            var item = new HistoryItem(entry, details, false, _ => "BIP", _ => null, sender, DateTimeOffset.UtcNow) { Expanded = true };
            main.History.Items.Add(item);
            main.History.GroupedItems.Add(new DateGroupHeader(DateTime.Today));
            main.History.GroupedItems.Add(item);
        }
    }

    private static void OpenPhone(Shell shell)
    {
        root?.Shell.StopTimers();
        window?.Close();
        root = new MobileRoot(shell);
        window = new Window { Content = root, Width = PhoneWidth, Height = PhoneHeight };
        window.Show();
        Pump();
    }

    private static bool Back()
    {
        Pump();
        var args = new RoutedEventArgs(TopLevel.BackRequestedEvent);
        window!.RaiseEvent(args);
        Pump();
        return args.Handled;
    }

    private static void VerifyScreen(string name)
    {
        Pump();
        MobileLayout.AdaptView(root!);
        Pump();
        CheckBounds(name);
        Shot(name);
    }

    private static void CheckBounds(string screen)
    {
        foreach (var control in root!.GetVisualDescendants().OfType<Control>())
        {
            if (!control.IsEffectivelyVisible || control.Bounds.Width <= 0 || control.Bounds.Height <= 0) continue;
            // Controls inside a horizontal ScrollViewer are meant to scroll horizontally.
            if (control.GetVisualAncestors().OfType<ScrollViewer>().Any(s => s.HorizontalScrollBarVisibility != Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled))
                continue;
            if (control is TextBlock or Button or TextBox or ComboBox or CheckBox or NumericUpDown)
            {
                var topLeft = control.TranslatePoint(new Point(0, 0), root!);
                var bottomRight = control.TranslatePoint(new Point(control.Bounds.Width, control.Bounds.Height), root!);
                if (topLeft is { } tl && bottomRight is { } br && (tl.X < -2 || br.X > PhoneWidth + 2))
                    Overflows.Add($"{screen}: {control.GetType().Name} ({DescribeControl(control)}) [{tl.X:0.#}..{br.X:0.#}] exceeds 0..{PhoneWidth}");
            }
        }
    }

    private static string DescribeControl(Control c) => c switch
    {
        TextBlock tb => Shown(tb) ?? "",
        Button b => b.Content?.ToString() ?? b.Name ?? "",
        TextBox tb => tb.PlaceholderText ?? tb.Name ?? "",
        _ => c.Name ?? c.GetType().Name,
    };

    private static void ScanEnglish(string where)
    {
        Pump();
        MobileLayout.AdaptView(root!);
        Pump();
        foreach (var block in root!.GetVisualDescendants().OfType<TextBlock>())
            if (block.IsEffectivelyVisible && Shown(block) is { } text && Localization.HasCyrillic(text) && text != "Русский"
                && !(block.DataContext is HistoryItem item && text == item.Payload))
                Untranslated.Add($"{where}: {text}");
    }

    private static string? Shown(TextBlock block) => block.Inlines is { Count: > 0 } inlines ? string.Concat(inlines.OfType<Run>().Select(r => r.Text)) : block.Text;

    private static void Pump()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }

    private static void Shot(string name)
    {
        Pump();
        try { window?.CaptureRenderedFrame()?.Save(Path.Combine(output, $"{++step:00}-{name}.png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }
        catch (ObjectDisposedException) { }
    }

    private static void Check(bool condition, string what) { if (!condition) throw new InvalidOperationException(what); }

    private static async Task<T> UntilAsync<T>(Func<T?> probe, string what, int seconds = 20) where T : class
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            Pump();
            if (probe() is { } value) return value;
            await Task.Delay(50);
        }
        throw new TimeoutException($"{what}: not reached in {seconds} s");
    }
}
