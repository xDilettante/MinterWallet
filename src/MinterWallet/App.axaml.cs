using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;

namespace MinterWallet;

public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Localization.Attach();
        AddressMarks.Attach();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new MainWindow();
            desktop.MainWindow = window;
            var tray = new TrayHost(this, desktop, window, (Shell)window.DataContext!);
            Program.Instance?.Listen(tray.Show);
        }
        base.OnFrameworkInitializationCompleted();
    }

    // "system" follows the OS; "dark" and "light" are fixed.
    public static void ApplyTheme(string? theme)
    {
        if (Current != null) Current.RequestedThemeVariant = theme switch { "dark" => ThemeVariant.Dark, "light" => ThemeVariant.Light, _ => ThemeVariant.Default };
    }
}
