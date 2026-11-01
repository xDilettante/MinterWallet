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
string? Option(string name) { var at = Array.IndexOf(args, name); return at >= 0 && at + 1 < args.Length ? args[at + 1] : null; }
var phoneSize = (Option("--size") ?? "412x915").Split('x').Select(double.Parse).ToArray();
var theme = Option("--theme") ?? "dark";
var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "artifacts", "mobile-checks"));
if (phoneSize[0] != 412) output += "-" + string.Join("x", phoneSize);
if (theme == "light") output += "-light";
Directory.CreateDirectory(output);
var session = HeadlessUnitTestSession.StartNew(typeof(MobileChecksApp));
var url = Option("--devnet") ?? Option("--mainnet");
var code = await session.Dispatch(() => MobileScenario.RunAsync(output, url, phoneSize[0], phoneSize[1], theme), CancellationToken.None);
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
    public bool ShowsClipboardConfirmation { get; set; }
    public string? BiometricSecret { get; private set; }
    public bool AutoApproveBiometrics { get; set; }
    public int ReadCalls { get; private set; }
    public int EnableCalls { get; private set; }
    public TaskCompletionSource<string?>? DeferredRead { get; set; }
    public bool CanScanQr => true;
    public TaskCompletionSource<string?>? DeferredScan { get; set; }
    public Task<string?> ScanQrAsync() => DeferredScan?.Task ?? Task.FromResult<string?>(null);
    public void CancelQrScan() => DeferredScan?.TrySetResult(null);

    public void Notify(string title, string text) => Notifications.Add((title, text));
    public void SetSecure(bool secure) => Secure = secure;
    public bool CanUseBiometrics => true;
    public bool HasBiometricSecret => !string.IsNullOrEmpty(BiometricSecret);
    public bool CanUsePin => true;
    public string DerivePinCredential(string pin) => Convert.ToHexStringLower(HMACSHA256.HashData("test-device-bound-key"u8, Encoding.UTF8.GetBytes(pin)));
    public Task<bool> EnableBiometricsAsync(string secret) { EnableCalls++; BiometricSecret = secret; return Task.FromResult(true); }
    public void ClearBiometricSecret() => BiometricSecret = null;
    public Task<string?> ReadBiometricSecretAsync(string title, string subtitle)
    {
        ReadCalls++;
        if (DeferredRead is { } deferred) { DeferredRead = null; return deferred.Task; }
        return Task.FromResult(AutoApproveBiometrics ? BiometricSecret : null);
    }
}

public static class MobileScenario
{
    private static double PhoneWidth = 412;
    private static double PhoneHeight = 915;
    private static string PhoneTheme = "dark";
    private const string Password = "mobile-check-пароль";
    private static Window? window;
    private static MobileRoot? root;
    private static string output = "";
    private static int step;
    private static readonly HashSet<string> Untranslated = [];
    private static readonly HashSet<string> Overflows = [];

    public static async Task<int> RunAsync(string outputDirectory, string? liveUrl, double width = 412, double height = 915, string theme = "dark")
    {
        PhoneWidth = width; PhoneHeight = height; PhoneTheme = theme;
        output = outputDirectory;
        foreach (var old in Directory.GetFiles(output, "*.png")) File.Delete(old);
        var data = Directory.CreateTempSubdirectory("minterwallet-mobile-").FullName;
        var enData = Directory.CreateTempSubdirectory("minterwallet-mobile-en-").FullName;
        var platform = new FakeMobilePlatform();
        MobileHost.Platform = platform;
        try
        {
            Check(MobileRoot.KeyboardOverlap(new Size(412, 915), new Rect(0, 615, 412, 300)) == 300, "overlay keyboard reserves its actual overlap");
            Check(MobileRoot.KeyboardOverlap(new Size(412, 615), new Rect(0, 615, 412, 300)) == 0, "resized viewport does not reserve the keyboard twice");
            VerifyQrDecoder();
            await LatePlatformFlowAsync();
            MobileHost.Platform = platform;
            await OfflineFlowAsync(data, platform, liveUrl);
            await EnglishFlowAsync(enData);
            await SecurityFlowAsync();
            await WalletMenuFlowAsync();
            if (Overflows.Count > 0)
                throw new InvalidOperationException($"Horizontal overflow on {PhoneWidth}px phone screen:\n" + string.Join("\n", Overflows));
            if (Untranslated.Count > 0)
                throw new InvalidOperationException("Untranslated Cyrillic in English mode:\n" + string.Join("\n", Untranslated));
            Console.WriteLine($"PASS mobile: {step} phone screens ({PhoneWidth}×{PhoneHeight}) rendered to {output}; zero horizontal overflow, zero untranslated Cyrillic, " +
                "onboarding, PIN/password migration, biometric opt-in/auto-confirm/fallback/cancellation, attempt limits, pull refresh, back gesture, FLAG_SECURE and notifications verified.");
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
            AutoLockMinutes = 10, Theme = PhoneTheme, CheckUpdates = false, Telemetry = false, Language = "ru",
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
        Check(root!.Security.SetupVisible && !platform.HasBiometricSecret, "biometrics require explicit opt-in after successful import");
        await root.Security.Enable.RunAsync();
        Check(shell.Settings.MobileBiometricsEnabled && platform.HasBiometricSecret, "explicit opt-in enables biometrics");

        // Populate sample balances, delegations, frozen funds and history so every card is exercised on the phone width
        PopulateSampleData(main);
        if (liveUrl != null)
            await UntilAsync(() => main.Balances.Count > 0 ? main : null, "live balances", 30);

        // 4. Overview, Derivation expanded + parameters, Receive QR stacked vertically
        VerifyScreen("overview");
        var receivingAddress = root!.GetVisualDescendants().OfType<TextBlock>().First(b => AddressMarks.GetFull(b) && b.IsEffectivelyVisible);
        Check(Shown(receivingAddress) == main.AddressText && !Shown(receivingAddress)!.Contains('…'), "main receiving address is displayed in full");
        Check(receivingAddress.TextTrimming == Avalonia.Media.TextTrimming.None && receivingAddress.TextWrapping == Avalonia.Media.TextWrapping.NoWrap, "full receiving address stays on one fitted line");
        Check(receivingAddress.TranslatePoint(new Point(receivingAddress.Bounds.Width, 0), root)?.X <= PhoneWidth - 12, "full address fits the available width after scaling");
        var overview = root.FindDescendantOfType<MobileOverviewView>()!;
        Check(root.FindDescendantOfType<MobileMainView>()!.FindControl<Border>("Header")!.Bounds.Height <= 84, "mobile header includes toolbar, server and statistics compactly");
        Check(!TopLevel.GetAutoSafeAreaPadding(root), "framework auto padding is disabled because the root owns safe-area and keyboard insets");
        var addressHeading = root.GetVisualDescendants().OfType<TextBlock>().Single(b => b.Text == "Адреса кошелька");
        var headingCenters = ((StackPanel)addressHeading.Parent!).Children.OfType<Control>()
            .Select(c => c.TranslatePoint(new Point(c.Bounds.Width / 2, c == addressHeading ? addressHeading.TextLayout.Height / 2 : c.Bounds.Height / 2), root)!.Value.Y).ToArray();
        Check(headingCenters.Max() - headingCenters.Min() < 1, "address header icon, label and help share one vertical center");
        var derivationToggle = root.FindDescendantOfType<MinterWallet.Views.DerivationPanel>()!.FindControl<Button>("ToggleAddresses")!;
        main.Derivation.Toggle.Execute(null);
        Pump();
        Check(derivationToggle.Content is Icon, "derivation toggle keeps its compact icon when expansion changes");
        main.Derivation.Toggle.Execute(null);
        Pump();
        Check(root.GetVisualDescendants().OfType<TextBlock>().Any(b => b.IsEffectivelyVisible && Shown(b) == main.UsageCompact), "online, daily and total statistics remain visible in the header");
        var walletMenu = root.FindDescendantOfType<MobileMainView>()!.FindControl<Button>("WalletMenu")!;
        var walletPoint = walletMenu.TranslatePoint(new Point(walletMenu.Bounds.Width / 2, walletMenu.Bounds.Height / 2), window!)!.Value;
        window!.MouseDown(walletPoint, Avalonia.Input.MouseButton.Left);
        window!.MouseUp(walletPoint, Avalonia.Input.MouseButton.Left);
        Pump();
        Check(walletMenu.Flyout!.IsOpen, "wallet selector stays open on its first tap");
        Check(Back() && main.IsOverview && !walletMenu.Flyout.IsOpen, "Back closes wallet selection without leaving overview");
        var quickActions = overview.GetVisualDescendants().OfType<Button>().Where(b => b.Classes.Contains("mobileQuickAction") && b.IsEffectivelyVisible).ToArray();
        Check(quickActions.Length == 4 && quickActions.All(b => b.Bounds.Height >= 44), "four touch-sized overview actions remain available");
        var assets = overview.FindControl<ItemsControl>("Assets")!;
        var visibleAssetText = assets.GetVisualDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible).Select(Shown).ToArray();
        Check(visibleAssetText.Contains("$18.50") && !visibleAssetText.Contains(main.Balances[0].BipText), "asset list shows fiat once without a duplicate composite estimate");
        var firstAsset = assets.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("mobileAssetRow"));
        Check(firstAsset.TranslatePoint(new Point(0, 24), root)?.Y < PhoneHeight - 58, "the first asset is visible above bottom navigation");
        string? copiedAddress = null;
        shell.CopyText = text => { copiedAddress = text; return Task.CompletedTask; };
        await main.CopyAddress.RunAsync();
        Check(copiedAddress == main.Selected!.Account.Address.ToString() && main.CopyLabel == "Скопировано", "overview copies the full selected address and shows success feedback");
        copiedAddress = null;
        Check(MobileAddressCopy.AddressOf(receivingAddress) == main.Address, "copy source retains the unshortened receiving address");
        var addressPoint = receivingAddress.TranslatePoint(new Point(receivingAddress.Bounds.Width / 2, receivingAddress.Bounds.Height / 2), window!)!.Value;
        window!.MouseDown(addressPoint, Avalonia.Input.MouseButton.Left); window!.MouseUp(addressPoint, Avalonia.Input.MouseButton.Left);
        Pump();
        Check(copiedAddress == main.Address && root.FindControl<Border>("CopyNotice")!.IsVisible, "tap copies the full address and shows feedback");
        platform.ShowsClipboardConfirmation = true;
        copiedAddress = null;
        await Task.Delay(600); // A second independent tap, outside the platform's double-tap interval.
        window!.MouseDown(addressPoint, Avalonia.Input.MouseButton.Left); window!.MouseUp(addressPoint, Avalonia.Input.MouseButton.Left);
        Pump();
        Check(copiedAddress == main.Address && !root.FindControl<Border>("CopyNotice")!.IsVisible, "native clipboard confirmation is not duplicated by an app notification");
        platform.ShowsClipboardConfirmation = false;
        var ambiguous = new TextBlock(); AddressMarks.SetText(ambiguous, main.Address + " → Mx37980a12b1be0855384b8d4f24b2d7957f680149");
        Check(MobileAddressCopy.AddressOf(ambiguous) == null, "ambiguous address lines are never copied as a guessed recipient");
        var shortened = new TextBlock(); AddressMarks.SetText(shortened, "Mx9858…da94");
        Check(MobileAddressCopy.AddressOf(shortened) == null, "a shortened address is never copied instead of the original");
        quickActions.Single(b => b.Command == null).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(main.IsBridge, "overview bridge action opens bridge for the current wallet");
        main.Section = Section.Overview;
        root.FindDescendantOfType<MobileMainView>()!.FindControl<Button>("NavStaking")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(main.IsStaking, "bottom navigation opens staking directly");
        main.Section = Section.Overview;
        main.Derivation.Toggle.Execute(null);
        VerifyScreen("derivation-cards");
        main.Derivation.Edit.Execute(null);
        VerifyScreen("derivation-params");
        main.Derivation.Edit.Execute(null);
        main.Derivation.Toggle.Execute(null);

        main.ShowReceive = true;
        main.Request.Amount = "150";
        main.Request.Message = "Оплата заказа";
        Check(main.Request.QrText == main.Address, "address mode ignores stored request fields until the user chooses a payment request");
        main.Request.Amount = ""; main.Request.Message = "";
        VerifyScreen("receive-qr");
        var receiveQr = root.FindDescendantOfType<MobileReceiveView>()!.FindControl<QrView>("ReceiveQr")!;
        Check(receiveQr.Text == main.AddressText && receiveQr.Bounds.Width >= 200, "receive sheet shows a readable QR for the selected address");
        var receiveEditor = root.FindDescendantOfType<MinterWallet.Views.PaymentRequestEditor>()!;
        var receiveMode = receiveEditor.FindControl<ListBox>("ReceiveMode")!;
        Check(!receiveEditor.FindControl<StackPanel>("RequestFields")!.IsVisible, "address-only mode keeps the form compact");
        receiveMode.SelectedIndex = 1; Pump();
        Check(main.Request.IsRequest && !main.Request.HasQr && receiveQr.Text == "", "empty payment request shows no misleading address QR");
        main.Request.Amount = "150,5"; main.Request.Coin = main.Request.Coins.Last(); main.Request.Message = "Заказ 7";
        VerifyScreen("receive-request");
        var decodedRequest = MinterLink.Parse(receiveQr.Text!);
        Check(decodedRequest?.DecodeData() is SendData asked && asked.To == main.Selected!.Account.Address && asked.Coin == main.Request.Coin.Id
            && asked.Value == Units.ParseCoins("150.5") && Encoding.UTF8.GetString(decodedRequest.Payload) == "Заказ 7", "receive QR includes the selected address, coin, exact amount and comment");
        Check(main.Request.QrCaption == $"Запрос на 150.5 {main.Request.Coin.Symbol}", "QR caption states the amount and coin");
        await main.Request.Copy.RunAsync();
        Check(copiedAddress == main.Request.Link, "request copy shares the same payment data as QR");
        main.Request.Amount = "-1"; Pump();
        copiedAddress = null;
        await main.Request.Copy.RunAsync();
        Check(!main.Request.HasQr && receiveQr.Text == "" && copiedAddress == null && main.Request.Problem.Length > 0, "invalid request hides QR and cannot be copied");
        main.Request.Amount = "150,5";
        main.Request.Message = new string('я', Transaction.MaxPayload / 2 + 1);
        Check(!main.Request.HasQr && main.Request.Problem.Length > 0, "oversized UTF-8 comment cannot produce a request QR");
        main.Request.Message = "Заказ 7";
        receiveMode.SelectedIndex = 0; Pump();
        Check(receiveQr.Text == main.Address && !main.Request.HasLink && main.Request.Message == "Заказ 7" && main.Request.Amount == "150,5", "switching to address restores a plain QR and preserves the draft");
        receiveMode.SelectedIndex = 1; Pump();
        Check(receiveQr.Text == main.Request.Link && main.Request.HasQr, "switching back restores the populated request");
        main.Request.ModeIndex = 0; main.Request.Amount = ""; main.Request.Message = "";
        Check(Back() && !main.ShowReceive, "back closes the receive sheet");
        main.ShowReceive = false;

        // 5. Send: single, multisend rows, check issue with QR
        main.Section = Section.Send;
        VerifyScreen("send-single");
        var qrRecipient = "Mx37980a12b1be0855384b8d4f24b2d7957f680149";
        Check(main.Send.ApplyQr(qrRecipient) && main.Send.Recipient == qrRecipient, "plain address QR fills recipient");
        var paymentQr = MinterLink.Of(new SendData(0, Address.Parse(qrRecipient), Units.ParseCoins("1.25")), Encoding.UTF8.GetBytes("QR request")).ToString();
        Check(main.Send.ApplyQr(paymentQr) && main.Send.Amount == "1.25" && main.Send.Payload == "QR request", "payment QR fills amount, coin and message without sending");
        var unavailableCoinQr = MinterLink.Of(new SendData(uint.MaxValue, Address.Parse(qrRecipient), Units.ParseCoins("1.25"))).ToString();
        Check(main.Send.ApplyQr(unavailableCoinQr) && main.Send.Coin == null, "request for a missing coin leaves the transfer coin unselected");
        main.Send.BalancesChanged(); main.Send.BalancesChanged();
        Check(main.Send.Coin == null, "balance refresh cannot replace an unavailable requested coin with another currency");
        main.Send.Coin = main.Send.Coins.First();
        main.Send.BalancesChanged();
        Check(main.Send.Coin?.Coin.Id == 0, "sender can explicitly choose a different currency");
        main.Send.ApplyQr(paymentQr);
        Check(root.FindDescendantOfType<MinterWallet.Views.SendPage>()!.FindControl<Button>("ScanQrButton")!.IsVisible, "QR scanner is available on camera-equipped mobile platforms");
        var cancelledScan = (main.Send.Recipient, main.Send.Amount, main.Send.Payload);
        await main.Send.ScanQr.RunAsync();
        Check(cancelledScan == (main.Send.Recipient, main.Send.Amount, main.Send.Payload), "cancelled scan preserves the payment draft");
        platform.DeferredScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var scan = main.Send.ScanQr.RunAsync();
        main.Send.Amount = "2.5";
        platform.DeferredScan.SetResult(paymentQr);
        await scan;
        Check(main.Send.Amount == "2.5", "late scan cannot overwrite a modified draft");
        platform.DeferredScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scan = main.Send.ScanQr.RunAsync();
        main.Section = Section.Overview;
        platform.DeferredScan.SetResult(paymentQr);
        await scan;
        Check(main.IsOverview && main.Send.Amount == "2.5", "late scan cannot navigate back or fill another screen");
        main.Section = Section.Send;
        platform.DeferredScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scan = main.Send.ScanQr.RunAsync();
        platform.DeferredScan.SetResult(paymentQr);
        await scan;
        platform.DeferredScan = null;
        Check(main.Send.Amount == "1.25", "camera result fills a current unchanged transfer draft");
        var qrDraft = (main.Send.Recipient, main.Send.Amount, main.Send.Payload, main.Send.Coin);
        foreach (var invalidQr in new[] { "https://example.com", "0x" + new string('1', 40), new string('x', 9000), new MinterLink(TxType.Delegate, [], []).ToString(),
            MinterLink.Of(new SendData(0, Address.Parse(qrRecipient), 0)).ToString(),
            MinterLink.Of(new SendData(0, Address.Parse(qrRecipient), 1), [0xff]).ToString(),
            MinterLink.Of(new SendData(0, Address.Parse(qrRecipient), 1), new byte[Transaction.MaxPayload + 1]).ToString() })
            Check(!main.Send.ApplyQr(invalidQr) && qrDraft == (main.Send.Recipient, main.Send.Amount, main.Send.Payload, main.Send.Coin), "unsupported QR preserves the payment draft");
        main.Send.Say("");

        var sendPrimary = root.FindDescendantOfType<MinterWallet.Views.SendPage>()!.FindControl<Button>("SendPrimary")!;
        Check(sendPrimary.TranslatePoint(new Point(0, sendPrimary.Bounds.Height), root)?.Y <= PhoneHeight - 58, "basic transfer action fits above bottom navigation without scrolling");
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
        main.Swap.ExactInput = false;
        main.Swap.Amount = "10";
        main.Swap.Buy.Selected = new CoinOption(3494, "USDTBSC", "", 0, "");
        VerifyScreen("swap-buy");
        var swapPage = root.FindDescendantOfType<MinterWallet.Views.SwapPage>()!;
        Check(swapPage.FindControl<TextBox>("BuyAmount")!.IsEffectivelyVisible && !swapPage.FindControl<TextBox>("SellAmount")!.IsEffectivelyVisible,
            "mobile buy mode edits the destination amount instead of the payment amount");
        main.Swap.ExactInput = true; main.Swap.Amount = "";
        Pump();
        Check(swapPage.FindControl<TextBox>("SellAmount")!.IsEffectivelyVisible && !swapPage.FindControl<TextBox>("BuyAmount")!.IsEffectivelyVisible,
            "mobile sell mode edits only the payment amount");
        var coinPicker = root!.GetVisualDescendants().OfType<MinterWallet.Views.CoinPicker>().First(p => p.IsEffectivelyVisible);
        var coinButton = coinPicker.FindControl<Button>("Open")!;
        coinButton.Flyout!.ShowAt(coinButton);
        Pump();
        Check(((Control)((Flyout)coinButton.Flyout).Content!).Width <= PhoneWidth - 48, "coin search popup fits the phone width");
        VerifyScreen("coin-search");
        coinButton.Flyout.Hide();
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
        var sectionHelp = mainView.FindControl<MobileHelpButton>("ContextHelp")!;
        Check(sectionHelp.IsVisible && sectionHelp.Text?.Contains("Мост", StringComparison.OrdinalIgnoreCase) == true, "bridge introduction remains accessible in contextual help");
        var helpPoint = sectionHelp.TranslatePoint(new Point(sectionHelp.Bounds.Width / 2, sectionHelp.Bounds.Height / 2), window!)!.Value;
        window!.MouseDown(helpPoint, Avalonia.Input.MouseButton.Left);
        window!.MouseUp(helpPoint, Avalonia.Input.MouseButton.Left);
        Pump();
        Check(sectionHelp.Flyout!.IsOpen && sectionHelp.Template != null, "tap opens a styled explanation button on phones");
        VerifyScreen("bridge-help");
        Check(Back() && main.IsBridge && !sectionHelp.Flyout.IsOpen, "Back dismisses help without leaving the operation");
        main.BridgeDirection = 1;
        VerifyScreen("bridge-in");
        main.BridgeDirection = 0;

        // 10. Staking: delegation and validator
        main.Section = Section.Staking;
        VerifyScreen("staking-delegation");
        Check(sectionHelp.IsVisible && sectionHelp.Text?.Contains("30", StringComparison.Ordinal) == true, "unstaking explanation is retained in help");
        main.Staking.ModeIndex = 1;
        VerifyScreen("staking-validator");
        main.Staking.ModeIndex = 0;

        // 11. Tokens: mine, create, pool
        main.Section = Section.Tokens;
        VerifyScreen("tokens-mine");
        main.Tokens.ModeIndex = 1;
        VerifyScreen("tokens-create");
        var tokenHelp = root.GetVisualDescendants().OfType<MobileHelpButton>().Single(b => b.Name == "TokenRulesHelp");
        tokenHelp.Flyout!.ShowAt(tokenHelp);
        Pump();
        Check(tokenHelp.Flyout is Flyout { Content: ScrollViewer { Content: Control rules } } && ReferenceEquals(rules.DataContext, main.Tokens), "token rule help retains dynamic pricing context");
        tokenHelp.Flyout.Hide();
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

        moreGrid.IsVisible = true;
        mainView.FindControl<Button>("AddWalletMenu")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(main.IsAccounts && !moreGrid.IsVisible, "adding a wallet closes the menu even when Accounts is already open");

        // 14. Settings: dark & light themes, tray section hidden
        main.Section = Section.Settings;
        VerifyScreen("settings-dark");
        foreach (var block in root!.GetVisualDescendants().OfType<TextBlock>())
            Check(!(block.IsEffectivelyVisible && block.Text is "Окно и трей" or "Window and tray"), "desktop tray settings are hidden on mobile");
        main.Settings.ThemeIndex = 2; // Light
        VerifyScreen("settings-light");
        main.Settings.ThemeIndex = PhoneTheme == "light" ? 2 : 1; // Return to the requested theme

        // 15. Confirmation bottom sheet (ConfirmSheet) + Biometric confirmation
        Check(platform.HasBiometricSecret && platform.BiometricSecret == Password, "biometric secret saved after wallet import");
        var sheet = root!.FindControl<ConfirmSheet>("Sheet")!;
        var confirmTask = sheet.AskAsync(new ConfirmRequest(
            "Перевод 125 BIP",
            [("Счёт", "Основной · Mx9858effd232b4033e47d90003d41ec34ecaeda94"), ("Получатель", "Mx37980a12b1be0855384b8d4f24b2d7957f680149"), ("Сумма", "125 BIP"), ("Комиссия", "0.01 BIP")],
            ["Вы ещё не отправляли монеты на этот адрес. Сверьте его по краям."]));
        VerifyScreen("confirm-sheet");
        var bioBtn = sheet.FindControl<Button>("BioButton")!;
        Check(bioBtn.IsVisible, "ConfirmSheet shows fingerprint button when biometric secret is saved");
        Check(Back(), "back gesture cancels confirmation sheet");
        Check(await confirmTask == null, "cancelled confirmation returns null");

        // Verify biometric confirm returns the saved password
        platform.AutoApproveBiometrics = true;
        var bioConfirmTask = sheet.AskAsync(new ConfirmRequest("Перевод 125 BIP", [("Сумма", "125 BIP")], []));
        Pump();
        bioBtn.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Check(await bioConfirmTask == Password, "fingerprint confirmation resolves with saved wallet password");
        platform.AutoApproveBiometrics = false;

        // 16. Lock & Unlock screen (including fingerprint unlock button)
        main.Section = Section.Send;
        var balancesBeforeSourceChange = main.Balances.ToArray();
        main.Send.ApplyQr(unavailableCoinQr);
        main.Disconnect();
        foreach (var balance in balancesBeforeSourceChange) main.Balances.Add(balance);
        main.Send.BalancesChanged();
        Check(main.Send.Coin == null, "changing the source cannot replace the currency of a prepared payment request");
        main.Send.ApplyQr(paymentQr);
        platform.DeferredScan = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var scanAtLock = main.Send.ScanQr.RunAsync();
        main.LockNow.Execute(null);
        await scanAtLock;
        Check(platform.DeferredScan.Task.IsCompleted && main.Send.Amount == "1.25", "locking cancels the native scanner without changing the draft");
        platform.DeferredScan = null;
        Check(shell.Page is UnlockViewModel, "wallet locked");
        var unlock = (UnlockViewModel)shell.Page;
        unlock.Forgot.Execute(null);
        VerifyScreen("unlock");
        var unlockBioBtn = root!.GetVisualDescendants().OfType<Button>().FirstOrDefault(b => b.Name == "BiometricUnlockButton");
        Check(unlockBioBtn is { IsVisible: true }, "Unlock screen shows fingerprint unlock button");
        platform.AutoApproveBiometrics = true;
        await root!.PromptBiometricUnlockAsync(unlock);
        var mainAfterBio = await UntilAsync(() => shell.Page as MainViewModel, "unlocked via fingerprint");
        Check(mainAfterBio != null, "wallet unlocked via fingerprint");
        platform.AutoApproveBiometrics = false;
    }

    private static async Task EnglishFlowAsync(string data)
    {
        new WalletSettings
        {
            Sources = [new SourceSettings { Name = "MinterGate", Url = "http://127.0.0.1:1" }],
            AutoLockMinutes = 10, Theme = PhoneTheme, CheckUpdates = false, Telemetry = false, Language = "en",
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
        root!.Security.Later.Execute(null);
        PopulateSampleData(main);
        main.ShowReceive = true;
        main.Request.ModeIndex = 1; main.Request.Amount = "150";
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

        var walletMenu = mainView.FindControl<Button>("WalletMenu")!;
        walletMenu.Flyout!.ShowAt(walletMenu); Pump();
        var walletList = mainView.FindControl<ListBox>("WalletList")!;
        walletList.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content is Icon { Kind: "trash" }).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Pump();
        if (walletMenu.Flyout is Flyout { Content: Control popup })
            foreach (var block in popup.GetVisualDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible))
                if (Shown(block) is { } text && Localization.HasCyrillic(text)) Untranslated.Add($"en-wallet-menu: {text}");
        walletMenu.Flyout.Hide();

        var sheet = root!.FindControl<ConfirmSheet>("Sheet")!;
        _ = sheet.AskAsync(new ConfirmRequest("Перевод", [("Сумма", "10 BIP")], []));
        ScanEnglish("en-confirm-sheet");
        sheet.Cancel();
    }

    private static void PopulateSampleData(MainViewModel main)
    {
        main.UsageOnline = "3"; main.UsageDay = "9"; main.UsageTotal = "24";
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
            main.Recent.Add(item);
            main.History.Items.Add(item);
            main.History.GroupedItems.Add(new DateGroupHeader(DateTime.Today));
            main.History.GroupedItems.Add(item);
        }
        main.TotalText = "220 253.5";
        main.TotalUsd = "$264.30";
        main.TotalUsdLarge = "$264.30";
        main.TotalRub = "24 580 ₽";
        main.TotalBipSubtitle = "≈ 220 253.5 BIP";
        main.Request.BalancesChanged();
        // Re-evaluate count-based visibility after injecting offline gallery data, just as a completed refresh does.
        main.Shell.Redraw();
    }

    private static async Task WalletMenuFlowAsync()
    {
        var data = Directory.CreateTempSubdirectory("minterwallet-menu-").FullName;
        try
        {
            MobileHost.Platform = new FakeMobilePlatform();
            new WalletSettings { Sources = [new SourceSettings { Url = "http://127.0.0.1:1" }], Telemetry = false, CheckUpdates = false, Language = "ru", Theme = PhoneTheme }.Save(data);
            var (store, session) = Keystore.Create(Keystore.DefaultPath(data), Password, new Keystore.KdfParameters(8192, 1, 1, RandomNumberGenerator.GetBytes(16)));
            using var ownedSession = session;
            var phrase = session.AddMnemonic("abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about", "Фраза с адресами");
            session.AddNextIndex(phrase.SecretId!, "Дочерний");
            session.AddPrivateKey(SHA256.HashData("wallet-menu-public-test-key"u8), "Второй кошелёк");
            var shell = new Shell(data) { AutoRefresh = false }; shell.Opened(store, session); OpenPhone(shell);
            var main = (MainViewModel)shell.Page;
            var view = root!.FindDescendantOfType<MobileMainView>()!;
            var menu = view.FindControl<Button>("WalletMenu")!;
            var list = view.FindControl<ListBox>("WalletList")!;
            var backup = view.FindControl<CheckBox>("WalletRecoverySaved")!;
            var remove = view.FindControl<Button>("DeleteWalletConfirm")!;
            var sheet = root!.FindControl<ConfirmSheet>("Sheet")!;
            void AskRemoval()
            {
                menu.Flyout!.ShowAt(menu); Pump();
                var trash = list.GetVisualDescendants().OfType<Button>().Single(b => b.IsEffectivelyVisible && b.Content is Icon { Kind: "trash" });
                trash.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            }
            AskRemoval();
            Check(main.AccountsPage.ConfirmingGroupRemove && !remove.IsEnabled && store.Accounts.Count == 3, "trash requires explicit backup acknowledgement and removes nothing immediately");
            VerifyScreen("wallet-delete-warning");
            main.AccountsPage.CancelRemove.Execute(null);
            Check(store.Accounts.Count == 3, "cancelling a wallet removal preserves every child address");
            AskRemoval(); backup.IsChecked = true; remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            Check(sheet.IsVisible, "wallet removal requests PIN/password or biometrics only after explicit confirmation");
            sheet.Cancel();
            await UntilAsync(() => !main.AccountsPage.DeleteWallet.IsRunning ? main : null, "cancelled removal completes");
            Check(store.Accounts.Count == 3, "cancelling authentication removes nothing");
            AskRemoval(); backup.IsChecked = true; remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            sheet.FindControl<TextBox>("PasswordBox")!.Text = Password;
            sheet.FindControl<Button>("ConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => store.Accounts.Count == 1 ? main : null, "phrase wallet removed after verified confirmation");
            Check(store.Accounts.All(a => a.SecretId != phrase.SecretId) && main.Derivation.Wallets.Count == 1, "whole phrase wallet and all children disappear together");
            AskRemoval(); backup.IsChecked = true; remove.RaiseEvent(new RoutedEventArgs(Button.ClickEvent)); Pump();
            sheet.FindControl<TextBox>("PasswordBox")!.Text = Password;
            sheet.FindControl<Button>("ConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => store.Accounts.Count == 0 && main.Selected == null ? main : null, "last wallet removal shows empty state");
            Check(Keystore.Load(shell.KeystorePath).Accounts.Count == 0, "empty encrypted vault persists after deleting the last wallet");
            VerifyScreen("wallet-empty");
            menu.Flyout!.ShowAt(menu); Pump();
            view.FindControl<Button>("AddWalletDropdown")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Check(main.IsAccounts && !menu.Flyout.IsOpen, "plus in wallet dropdown opens wallet creation even when the list is empty");
        }
        finally
        {
            root?.Shell.StopTimers(); window?.Close();
            try { Directory.Delete(data, true); } catch (IOException) { }
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

    private static async Task LatePlatformFlowAsync()
    {
        var data = Directory.CreateTempSubdirectory("minterwallet-late-platform-").FullName;
        try
        {
            MobileHost.Platform = null;
            new WalletSettings { Sources = [new SourceSettings { Url = "http://127.0.0.1:1" }], Telemetry = false, CheckUpdates = false, Theme = PhoneTheme }.Save(data);
            var shell = new Shell(data) { AutoRefresh = false };
            OpenPhone(shell);
            ((WelcomeViewModel)shell.Page).Create.Execute(null);
            var create = (CreateWalletViewModel)shell.Page;
            create.Skip.Execute(null);
            MobileHost.Platform = new FakeMobilePlatform();
            Pump();
            Check(create.Credentials.CanChoose, "PIN capability updates when Android activity arrives after the root view");
            var selector = root!.GetVisualDescendants().OfType<ListBox>().Single(b => b.Name == "CredentialMode");
            Check(selector.IsEffectivelyVisible && selector.Bounds.Width > 0, "creation screen visibly offers PIN/password after late Android initialization");
            selector.SelectedIndex = 0;
            Pump();
            Check(create.Credentials.UsePin, "visible creation selector changes the actual protection mode");
            VerifyScreen("create-pin");
            create.Password = create.Repeat = "123456";
            await create.Finish.RunAsync();
            Check(shell.Page is MainViewModel && shell.Store?.CredentialKind == "android-pin-v1", "a six-digit PIN from the visible creation control creates the wallet");
        }
        finally { root?.Shell.StopTimers(); window?.Close(); Directory.Delete(data, true); }
    }

    private static async Task SecurityFlowAsync()
    {
        var data = Directory.CreateTempSubdirectory("minterwallet-mobile-security-").FullName;
        var platform = new FakeMobilePlatform();
        MobileHost.Platform = platform;
        try
        {
            new WalletSettings { Sources = [new SourceSettings { Url = "http://127.0.0.1:1" }], CheckUpdates = false, Telemetry = false, Language = "ru" }.Save(data);
            var shell = new Shell(data) { AutoRefresh = false };
            OpenPhone(shell);
            ((WelcomeViewModel)shell.Page).Restore.Execute(null);
            var import = (ImportViewModel)shell.Page;
            Check(import.Credentials.CanChoose, "phone offers PIN or password");
            import.Credentials.ModeIndex = 0;
            import.Secret = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
            import.Password = import.Repeat = "12345";
            await import.Finish.RunAsync();
            Check(!File.Exists(shell.KeystorePath), "short PIN cannot create a store");
            import.Password = import.Repeat = "123456";
            await import.Finish.RunAsync();
            Check(shell.Page is MainViewModel && shell.Store!.CredentialKind == "android-pin-v1", "PIN import opens the wallet");
            Check(Keystore.Load(shell.KeystorePath).CredentialKind == "android-pin-v1", "PIN metadata survives reload");
            Check(!File.ReadAllText(shell.KeystorePath).Contains("123456", StringComparison.Ordinal), "PIN not written in plaintext");
            var main = (MainViewModel)shell.Page;
            Check(root!.Security.SetupVisible && !platform.HasBiometricSecret, "PIN setup offers optional biometrics without saving a secret");
            VerifyScreen("security-opt-in");
            root.Security.Later.Execute(null);
            Check(!shell.Settings.MobileBiometricsEnabled && platform.EnableCalls == 0, "declining biometrics stores no secret");
            Check(root.GetVisualDescendants().OfType<RefreshContainer>().Any(), "overview has native pull-to-refresh");

            for (var i = 0; i < 5; i++)
                try { await shell.Credentials.VerifyAsync("999999"); throw new InvalidOperationException("wrong PIN accepted"); }
                catch (WrongPasswordException) { }
            Check(WalletSettings.Load(data).MobileAuthBlockedUntil > DateTimeOffset.UtcNow, "attempt delay survives restart");
            var blocked = false;
            try { await shell.Credentials.VerifyAsync("123456"); }
            catch (InvalidOperationException) { blocked = true; }
            Check(blocked, "attempt delay covers correct PIN too");
            shell.Settings.MobileAuthBlockedUntil = DateTimeOffset.UtcNow.AddSeconds(-1);
            var actual = await shell.Credentials.VerifyAsync("123456");
            Check(actual.Length == 64 && actual != "123456" && shell.Settings.MobileAuthFailures == 0, "PIN uses device-bound secret and success resets failures");
            using (shell.Store!.Unlock(actual)) { }

            var enabling = root.Security.Enable.RunAsync();
            Pump();
            var sheet = root.FindControl<ConfirmSheet>("Sheet")!;
            sheet.FindControl<TextBox>("PasswordBox")!.Text = "123456";
            sheet.FindControl<Button>("ConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await enabling;
            Check(shell.Settings.MobileBiometricsEnabled && platform.BiometricSecret == actual, "enabling from settings verifies PIN and stores only the derived secret");

            platform.AutoApproveBiometrics = true;
            var unlockReads = platform.ReadCalls;
            shell.Lock();
            shell = new Shell(data) { AutoRefresh = false };
            OpenPhone(shell);
            main = await UntilAsync(() => shell.Page as MainViewModel, "PIN wallet automatically unlocked on startup using biometric secret");
            Check(platform.ReadCalls > unlockReads, "startup requests biometrics without pressing a button");
            sheet = root!.FindControl<ConfirmSheet>("Sheet")!;
            var reads = platform.ReadCalls;
            var automatic = sheet.AskAsync(new ConfirmRequest("Автоматическое подтверждение", [], []));
            Pump();
            Check(platform.ReadCalls > reads, "confirmation starts biometrics without another button press");
            Check(await automatic == actual, "automatic biometric confirmation returns verified credential");

            platform.AutoApproveBiometrics = false;
            var bad = sheet.AskAsync(new ConfirmRequest("Неверный PIN", [], []));
            Pump();
            sheet.FindControl<TextBox>("PasswordBox")!.Text = "111111";
            sheet.FindControl<Button>("ConfirmButton")!.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            await UntilAsync(() => sheet.FindControl<TextBlock>("AuthStatus")!.Text?.Contains("Неверный", StringComparison.Ordinal) == true ? sheet : null, "incorrect PIN shown in confirmation");
            Check(!bad.IsCompleted && platform.BiometricSecret == actual && platform.EnableCalls == 1, "incorrect PIN neither confirms nor replaces saved biometric secret");
            sheet.Cancel();

            var delayed = new TaskCompletionSource<string?>();
            platform.DeferredRead = delayed;
            var old = sheet.AskAsync(new ConfirmRequest("Отменённая операция", [], []));
            Pump();
            sheet.Cancel();
            var newer = sheet.AskAsync(new ConfirmRequest("Другая операция", [], []));
            Pump();
            delayed.SetResult(actual);
            await Task.Delay(100);
            Pump();
            Check(await old == null && !newer.IsCompleted, "late biometric response cannot confirm a newer request");
            sheet.Cancel();

            main.Section = Section.Settings;
            Pump();
            root!.GetVisualDescendants().OfType<Expander>().Single(e => e.Name == "CredentialChangePanel").IsExpanded = true;
            Pump();
            main.Settings.Credentials.ModeIndex = 1;
            main.Settings.CurrentPassword = "123456";
            main.Settings.NewPassword = main.Settings.RepeatPassword = "replacement-password";
            await main.Settings.ChangePassword.RunAsync();
            Check(shell.Store!.CredentialKind == "password" && !platform.HasBiometricSecret, "PIN to password migration clears old biometric secret");
            Pump();
            var currentInput = root.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "CurrentCredentialInput");
            Check(currentInput.PlaceholderText == "Введите пароль.", "credential labels immediately follow the saved mode");
            root.Security.Later.Execute(null);
            shell.Lock();
            var reloaded = new Shell(data) { AutoRefresh = false };
            OpenPhone(reloaded);
            Check(!reloaded.Credentials.UsesPin, "restarted wallet uses new password method");
            var unlock = (UnlockViewModel)reloaded.Page;
            unlock.Password = "replacement-password";
            await unlock.Unlock.RunAsync();
            Check(reloaded.Page is MainViewModel, "password unlock after PIN migration succeeds");
            reloaded.Lock();
            reloaded.StartOver();
            Check(!platform.HasBiometricSecret && !reloaded.Settings.MobileBiometricsAsked, "recovery resets biometric consent for the new wallet");
        }
        finally
        {
            root?.Shell.StopTimers();
            window?.Close();
            try { Directory.Delete(data, true); } catch (IOException) { }
        }
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
        foreach (var footer in root!.GetVisualDescendants().OfType<Border>().Where(b => b.IsEffectivelyVisible && b.Classes.Contains("mobileFooter")))
        {
            if (footer.TranslatePoint(new Point(0, footer.Bounds.Height), root!) is { } bottom)
                Check(bottom.Y <= PhoneHeight + 1, "onboarding primary action remains in the viewport");
        }
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
                if (control is TextBlock addrBlock && !addrBlock.Classes.Contains("prose") && !AddressMarks.GetFull(addrBlock) && AddressMarks.GetText(addrBlock) is { Length: > 0 } && addrBlock.Bounds.Height > 28)
                    Overflows.Add($"{screen}: Address TextBlock wrapped onto multiple lines (height={addrBlock.Bounds.Height:0.#}px): {DescribeControl(addrBlock)}");
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

    private static void VerifyQrDecoder()
    {
        var address = "Mx37980a12b1be0855384b8d4f24b2d7957f680149";
        File.WriteAllBytes(Path.Combine(output, "scan-fixture.png"), QRCoder.PngByteQRCodeHelper.GetQRCode(address, QRCoder.QRCodeGenerator.ECCLevel.M, 10));
        foreach (var text in new[] { address, MinterLink.Of(new SendData(0, Address.Parse(address), Units.ParseCoins("1.25")), Encoding.UTF8.GetBytes("Оплата QR")).ToString() })
        {
            using var qr = QRCoder.QRCodeGenerator.GenerateQrCode(text, QRCoder.QRCodeGenerator.ECCLevel.M);
            var n = qr.ModuleMatrix.Count;
            var width = n * 5;
            var pixels = new byte[width * width];
            for (var y = 0; y < width; y++)
                for (var x = 0; x < width; x++)
                    pixels[y * width + x] = qr.ModuleMatrix[y / 5][x / 5] ? (byte)0 : (byte)255;
            for (var turn = 0; turn < 4; turn++)
            {
                Check(QrDecoder.Decode(pixels, width, width) == text, "camera decoder reads wallet QR in every orientation");
                Check(QrDecoder.Decode(pixels.Select(b => (byte)(255 - b)).ToArray(), width, width) == text, "camera decoder reads inverted QR");
                var rotated = new byte[pixels.Length];
                for (var y = 0; y < width; y++)
                    for (var x = 0; x < width; x++) rotated[x * width + width - y - 1] = pixels[y * width + x];
                pixels = rotated;
            }
        }
        Check(QrDecoder.Decode([], 0, 0) == null && QrDecoder.Decode([0], 2, 2) == null, "decoder rejects invalid frame dimensions");
        Check(QrDecoder.Decode(new byte[320 * 240], 320, 240) == null, "empty camera frame produces no result");
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
