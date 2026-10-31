using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;

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
        shell.SaveText ??= async (name, text) =>
        {
            var isJson = Path.GetExtension(name).Equals(".json", StringComparison.OrdinalIgnoreCase);
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                SuggestedFileName = name, DefaultExtension = Path.GetExtension(name).TrimStart('.'),
                FileTypeChoices = [new FilePickerFileType(isJson ? "JSON" : "CSV") { Patterns = ["*" + Path.GetExtension(name)] }],
            });
            if (file == null) return false;
            await using var stream = await file.OpenWriteAsync();
            stream.SetLength(0);
            // With the UTF-8 mark a spreadsheet reads the Cyrillic right; JSON stays clean RFC 8259 UTF-8.
            if (!isJson) await stream.WriteAsync(System.Text.Encoding.UTF8.GetPreamble());
            await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text));
            return true;
        };
        shell.OpenText ??= async () =>
        {
            var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Localization.Text("Профиль путей кошелька"), AllowMultiple = false,
                FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }],
            });
            if (files.Count == 0) return null;
            await using var stream = await files[0].OpenReadAsync();
            if (stream.Length > 1024 * 1024) throw new InvalidDataException("Профиль слишком большой (больше 1 МБ).");
            using var reader = new StreamReader(stream);
            return await reader.ReadToEndAsync();
        };
        DataContext = shell;
        shell.StartUsage();
        shell.StartWatching();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty || e.Property == WindowStateProperty) shell.WindowShown = IsVisible && WindowState != WindowState.Minimized;
        };
        Closed += (_, _) => shell.StopTimers();
        // Any input counts as activity for the auto-lock.
        AddHandler(PointerMovedEvent, (_, _) => Touch(shell), handledEventsToo: true);
        AddHandler(KeyDownEvent, (_, _) => Touch(shell), handledEventsToo: true);
    }

    private static void Touch(Shell shell) { if (shell.Page is MainViewModel main) main.LastActivity = DateTime.UtcNow; }
}
