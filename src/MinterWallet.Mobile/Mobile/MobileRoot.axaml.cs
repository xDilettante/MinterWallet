using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Platform;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;

namespace MinterWallet.Mobile;

// What MainWindow does on the desktop, for a phone: the shell's services (confirmation, clipboard, links, files) over
// the view's TopLevel, the system's back gesture, the app going to the background and coming back, the safe area
// (status bar, gesture bar, cutouts) and the on-screen keyboard.
public partial class MobileRoot : UserControl
{
    public Shell Shell { get; }
    private TopLevel? top;
    private Thickness safe;
    private double keyboard;

    public MobileRoot() : this(new Shell(Program.DataDirectory)) { }

    public MobileRoot(Shell shell)
    {
        Shell = shell;
        Localization.Use(shell.Settings.Language);
        Display.Use(shell.Settings);
        InitializeComponent();
        App.ApplyTheme(shell.Settings.Theme);
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
        if (Application.Current?.TryGetFeature<IActivatableLifetime>() is { } lifetime)
        {
            lifetime.Deactivated += (_, e) => { if (e.Kind == ActivationKind.Background) shell.WindowShown = false; };
            lifetime.Activated += (_, e) => { if (e.Kind == ActivationKind.Background) Resumed(); };
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        top = TopLevel.GetTopLevel(this);
        if (top == null) return;
        top.BackRequested += OnBack;
        if (top.InsetsManager is { } insets)
        {
            safe = insets.SafeAreaPadding;
            insets.SafeAreaChanged += (_, args) => { safe = args.SafeAreaPadding; Pad(); };
        }
        if (top.InputPane is { } pane)
            pane.StateChanged += (_, args) => { keyboard = args.NewState == InputPaneState.Open ? args.EndRect.Height : 0; Pad(); };
        Pad();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (top != null) top.BackRequested -= OnBack;
        top = null;
        base.OnDetachedFromVisualTree(e);
    }

    // The content stays clear of the system bars and of the keyboard while it is open.
    private void Pad() => SafeArea.Margin = new Thickness(safe.Left, safe.Top, safe.Right, Math.Max(safe.Bottom, keyboard));

    private void Touch() { if (Shell.Page is MainViewModel main) main.LastActivity = DateTime.UtcNow; }

    private void WatchPage()
    {
        if (Shell.Page is MainViewModel main)
        {
            main.PropertyChanged += (_, _) => UpdateSecure();
            main.AccountsPage.PropertyChanged += (_, _) => UpdateSecure();
        }
        UpdateSecure();
    }

    private void UpdateSecure() => MobileHost.Platform?.SetSecure(Shell.Page is CreateWalletViewModel or ImportViewModel
        || Shell.Page is MainViewModel { IsAccounts: true });

    // Back from the background: the auto-lock counts the time away too (the timer may not have run meanwhile).
    private void Resumed()
    {
        Shell.WindowShown = true;
        if (Shell.Page is MainViewModel main && Shell.Settings.AutoLockMinutes > 0 && DateTime.UtcNow - main.LastActivity > TimeSpan.FromMinutes(Shell.Settings.AutoLockMinutes))
            Shell.Lock();
    }

    // The system's back: closes what is open over the page, then steps back; on the wallet's first screen the OS takes it.
    private void OnBack(object? sender, RoutedEventArgs e)
    {
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
