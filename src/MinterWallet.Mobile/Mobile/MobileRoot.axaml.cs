using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using MinterWallet.Views;

namespace MinterWallet.Mobile;

// What MainWindow does on the desktop, for a phone: the shell's services (confirmation, clipboard, links, files) over
// the view's TopLevel, the system's back gesture, the app going to the background and coming back, the safe area
// (status bar, gesture bar, cutouts), the on-screen keyboard and hardware biometric unlock.
public partial class MobileRoot : UserControl
{
    public Shell Shell { get; }
    private TopLevel? top;
    private Rect keyboard;
    public MobileSecurity Security { get; }
    private bool promptingBio;
    private int copyNoticeGeneration;

    public MobileRoot() : this(new Shell(Program.DataDirectory)) { }

    public MobileRoot(Shell shell)
    {
        Shell = shell;
        shell.Credentials.Mobile = true;
        ConfigurePlatform();
        MobileHost.PlatformChanged += ConfigurePlatform;
        Security = new MobileSecurity(shell);
        Localization.Use(shell.Settings.Language);
        Display.Use(shell.Settings);
        InitializeComponent();
        App.ApplyTheme(shell.Settings.Theme);
        Sheet.Shell = shell;
        BiometricSetup.DataContext = Security;
        Security.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(MobileSecurity.SetupVisible)) BiometricSetup.IsVisible = Security.SetupVisible; };
        shell.Confirm ??= request => Sheet.AskAsync(request);
        shell.CopyText ??= async text => { if (top?.Clipboard is { } clipboard) await clipboard.SetTextAsync(text); };
        shell.PasteText ??= async () => top?.Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        shell.OpenUrl ??= async uri => { if (top != null) await top.Launcher.LaunchUriAsync(uri); };
        shell.SaveText ??= SaveTextAsync;
        shell.OpenText ??= OpenTextAsync;
        shell.Arrived += arrival =>
        {
            var (title, text) = Shell.Describe(arrival);
            MobileHost.Platform?.Notify(title, text);
        };
        DataContext = shell;
        shell.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(Shell.Page)) WatchPage(); };
        WatchPage();
        shell.StartUsage();
        shell.StartWatching();
        // Any touch or key counts as activity for the auto-lock.
        AddHandler(PointerPressedEvent, (_, _) => Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => Touch(), RoutingStrategies.Tunnel, handledEventsToo: true);
        AddHandler(InputElement.TappedEvent, OnAddressTapped, RoutingStrategies.Bubble, handledEventsToo: true);
        if (Application.Current?.TryGetFeature<IActivatableLifetime>() is { } lifetime)
        {
            lifetime.Deactivated += (_, e) => { if (e.Kind == ActivationKind.Background) shell.WindowShown = false; };
            lifetime.Activated += (_, e) => { if (e.Kind == ActivationKind.Background) Resumed(); };
        }
    }

    private async void OnAddressTapped(object? sender, TappedEventArgs e)
    {
        if (Shell.Page is not MainViewModel main || Sheet.IsVisible || Security.SetupVisible || Shell.CopyText == null) return;
        var text = e.Source as TextBlock ?? (e.Source as Visual)?.GetVisualAncestors().OfType<TextBlock>().FirstOrDefault();
        if (text == null || text.Classes.Contains("prose") || text.GetVisualAncestors().OfType<Button>().Any()
            || MobileAddressCopy.AddressOf(text) is not { } address) return;
        if (text.DataContext is DerivedItem or DerivationCard) return;
        e.Handled = true;
        try
        {
            if (text.DataContext is BridgeDepositViewModel && !main.Derivation.PrepareReceive()) return;
            // The carousel remains a selector. Own addresses retain the existing receive/path protection.
            if (Minter.Protocol.Address.TryParse(address, out var own) && (main.Accounts.Any(a => a.Account.Address == own) || main.Selected?.Account.Address == own))
                if (main.Selected?.Account.Address != own || !main.Derivation.PrepareReceive()) return;
            await Shell.CopyText(address);
            if (!ReferenceEquals(Shell.Page, main)) return;
            var generation = ++copyNoticeGeneration;
            CopyNotice.IsVisible = MobileHost.Platform?.ShowsClipboardConfirmation != true;
            if (CopyNotice.IsVisible)
                DispatcherTimer.RunOnce(() => { if (generation == copyNoticeGeneration) CopyNotice.IsVisible = false; }, TimeSpan.FromSeconds(2));
        }
        catch (Exception error) { main.Fail(error); }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MobileHost.PlatformChanged -= ConfigurePlatform;
        MobileHost.PlatformChanged += ConfigurePlatform;
        ConfigurePlatform();
        top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        top.BackRequested += OnBack;
        top.SizeChanged += OnTopSizeChanged;
        top.ScalingChanged += OnScalingChanged;
        if (top.InsetsManager is { } insets)
            insets.SafeAreaChanged += OnSafeAreaChanged;
        if (top.InputPane is { } pane)
            pane.StateChanged += OnInputPaneChanged;
        Pad();
        if (Shell.Page is UnlockViewModel unlock && Shell.Settings.MobileBiometricsEnabled)
            Dispatcher.UIThread.Post(() => _ = PromptBiometricUnlockAsync(unlock), DispatcherPriority.Background);
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (top != null)
        {
            top.BackRequested -= OnBack;
            top.SizeChanged -= OnTopSizeChanged;
            top.ScalingChanged -= OnScalingChanged;
            if (top.InsetsManager is { } insets) insets.SafeAreaChanged -= OnSafeAreaChanged;
            if (top.InputPane is { } pane) pane.StateChanged -= OnInputPaneChanged;
        }
        MobileHost.PlatformChanged -= ConfigurePlatform;
        Sheet.Cancel();
        Security.Dismiss();
        MobileHost.Platform?.CancelBiometrics();
        top = null;
        base.OnDetachedFromVisualTree(e);
    }

    // The content stays clear of the system bars and of the keyboard while it is open.
    public static double KeyboardOverlap(Size clientSize, Rect keyboardBounds) => new Rect(clientSize).Intersect(keyboardBounds).Height;

    private void Pad()
    {
        // Android can publish its first insets before the screen density is available. Read them again at the
        // current scale instead of retaining the initial values (physical pixels mistaken for layout units).
        var safe = top?.InsetsManager?.SafeAreaPadding ?? default;
        SafeArea.Margin = new Thickness(safe.Left, safe.Top, safe.Right, Math.Max(safe.Bottom, top == null ? 0 : KeyboardOverlap(top.ClientSize, keyboard)));
    }
    private void OnSafeAreaChanged(object? sender, SafeAreaChangedArgs e) => Pad();
    private void OnScalingChanged(object? sender, EventArgs e) => Pad();
    private void OnTopSizeChanged(object? sender, SizeChangedEventArgs e) => Pad();
    private void OnInputPaneChanged(object? sender, InputPaneStateEventArgs e)
    {
        keyboard = e.NewState == InputPaneState.Open ? e.EndRect : default;
        Pad();
        Dispatcher.UIThread.Post(() => (top?.FocusManager?.GetFocusedElement() as Control)?.BringIntoView(), DispatcherPriority.Background);
    }

    private void Touch() { if (Shell.Page is MainViewModel main) main.LastActivity = DateTime.UtcNow; }

    // Avalonia can create its root during Application.OnCreate, before MainActivity publishes Android services.
    private void ConfigurePlatform() => Shell.Credentials.DerivePin = MobileHost.Platform is { CanUsePin: true } platform ? platform.DerivePinCredential : null;

    private void WatchPage()
    {
        CopyNotice.IsVisible = false; copyNoticeGeneration++;
        if (Shell.Page is UnlockViewModel unlock && Shell.Settings.MobileBiometricsEnabled)
        {
            if (MobileHost.Platform is { CanUseBiometrics: true, HasBiometricSecret: true })
                Dispatcher.UIThread.Post(() => _ = PromptBiometricUnlockAsync(unlock), DispatcherPriority.Background);
            else unlock.Say("Биометрия недоступна. Введите PIN-код или пароль.", Tone.Warning);
        }
        if (Shell.Page is not MainViewModel) { Sheet.Cancel(); Security.Dismiss(); MobileHost.Platform?.CancelBiometrics(); }
        if (Shell.Page is MainViewModel main)
        {
            main.PropertyChanged += (_, _) => UpdateSecure();
            main.AccountsPage.PropertyChanged += (_, _) => UpdateSecure();
        }
        UpdateSecure();
    }

    public async Task PromptBiometricUnlockAsync(UnlockViewModel unlock)
    {
        if (top == null || promptingBio || Shell.Page != unlock || !Shell.Settings.MobileBiometricsEnabled || MobileHost.Platform is not { CanUseBiometrics: true, HasBiometricSecret: true } platform) return;
        promptingBio = true;
        try
        {
            var secret = await platform.ReadBiometricSecretAsync("Minter Wallet", Localization.Text("Подтвердите вход в кошелёк"));
            if (Shell.Page != unlock) return;
            if (!string.IsNullOrEmpty(secret))
            {
                if (await unlock.UnlockBiometricAsync(secret)) return;
                platform.ClearBiometricSecret();
            }
            unlock.Say(platform.BiometricError, Tone.Warning);
            this.FindDescendantOfType<MobileUnlockView>()?.FindControl<TextBox>("PasswordBox")?.Focus();
        }
        finally { promptingBio = false; }
    }

    private void UpdateSecure() => MobileHost.Platform?.SetSecure(Shell.Page is CreateWalletViewModel or ImportViewModel
        || Shell.Page is MainViewModel { IsAccounts: true });

    // Back from the background: the auto-lock counts the time away too (the timer may not have run meanwhile).
    private void Resumed()
    {
        Shell.WindowShown = true;
        _ = Shell.PingUsageAsync();
        if (Shell.Page is MainViewModel main && Shell.Settings.AutoLockMinutes > 0 && DateTime.UtcNow - main.LastActivity > TimeSpan.FromMinutes(Shell.Settings.AutoLockMinutes))
            Shell.Lock();
        else if (Shell.Page is UnlockViewModel unlock && Shell.Settings.MobileBiometricsEnabled)
            Dispatcher.UIThread.Post(() => _ = PromptBiometricUnlockAsync(unlock), DispatcherPriority.Background);
    }

    // The system's back: closes what is open over the page, then steps back; on the wallet's first screen the OS takes it.
    private void OnBack(object? sender, RoutedEventArgs e)
    {
        if (Security.SetupVisible) { Security.Later.Execute(null); e.Handled = true; return; }
        if (Sheet.IsVisible) { Sheet.Cancel(); e.Handled = true; return; }
        switch (Shell.Page)
        {
            case MainViewModel main when this.FindDescendantOfType<MobileMainView>() is { } view && view.Back(main):
            case CreateWalletViewModel create when Run(create.Back):
            case ImportViewModel import when Run(import.Back):
                e.Handled = true;
                break;
        }
    }

    private static bool Run(System.Windows.Input.ICommand command)
    {
        if (!command.CanExecute(null)) return false;
        command.Execute(null);
        return true;
    }

    private async Task<bool> SaveTextAsync(string name, string text)
    {
        if (top == null) return false;
        var isJson = Path.GetExtension(name).Equals(".json", StringComparison.OrdinalIgnoreCase);
        var file = await top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = name, DefaultExtension = Path.GetExtension(name).TrimStart('.'),
            FileTypeChoices = [new FilePickerFileType(isJson ? "JSON" : "CSV") { Patterns = ["*" + Path.GetExtension(name)], MimeTypes = [isJson ? "application/json" : "text/csv"] }],
        });
        if (file == null) return false;
        await using var stream = await file.OpenWriteAsync();
        if (stream.CanSeek) stream.SetLength(0);
        // With the UTF-8 mark a spreadsheet reads the Cyrillic right; JSON stays clean RFC 8259 UTF-8.
        if (!isJson) await stream.WriteAsync(System.Text.Encoding.UTF8.GetPreamble());
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
        return true;
    }

    private async Task<string?> OpenTextAsync()
    {
        if (top == null) return null;
        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Localization.Text("Профиль путей кошелька"), AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"], MimeTypes = ["application/json", "text/plain", "application/octet-stream"] }],
        });
        if (files.Count == 0) return null;
        await using var stream = await files[0].OpenReadAsync();
        // Content providers may not know the length: read at most 1 MB and one byte more.
        var buffer = new byte[1024 * 1024 + 1];
        var read = 0;
        while (read < buffer.Length && await stream.ReadAsync(buffer.AsMemory(read)) is var n && n > 0) read += n;
        if (read > 1024 * 1024) throw new InvalidDataException("Профиль слишком большой (больше 1 МБ).");
        return System.Text.Encoding.UTF8.GetString(buffer, 0, read);
    }
}
