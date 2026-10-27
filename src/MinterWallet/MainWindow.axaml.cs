using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;

namespace MinterWallet;

public partial class MainWindow : Window
{
    public MainWindow() : this(new Shell(Program.DataDirectory)) { }

    public MainWindow(Shell shell)
    {
        Localization.Use(shell.Settings.Language);
        Display.Use(shell.Settings);
        InitializeComponent();
        App.ApplyTheme(shell.Settings.Theme);
        shell.Confirm ??= request => new ConfirmWindow(request).ShowDialog<string?>(this);
        shell.CopyText ??= async text => { if (Clipboard != null) await Clipboard.SetTextAsync(text); };
        shell.PasteText ??= async () => Clipboard == null ? null : await Clipboard.TryGetTextAsync();
        shell.OpenUrl ??= async uri => await Launcher.LaunchUriAsync(uri);
        DataContext = shell;
        // Any input counts as activity for the auto-lock.
        AddHandler(PointerMovedEvent, (_, _) => Touch(shell), handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => Touch(shell), handledEventsToo: true);
    }

    private static void Touch(Shell shell) { if (shell.Page is MainViewModel main) main.LastActivity = DateTime.UtcNow; }
}
