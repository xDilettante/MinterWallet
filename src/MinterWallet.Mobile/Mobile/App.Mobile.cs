using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Styling;
using MinterWallet.Mobile;

namespace MinterWallet;

// The phone's App (App.axaml is shared with the desktop): one view instead of a window, no tray. Under a desktop
// lifetime (a preview on the PC) the same view sits in a phone-sized window.
public partial class App : Application
{
    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Styles.Add(new MobileStyles());
        MobileLayout.Attach();
        Localization.Attach();
        AddressMarks.Attach();
    }

    public override void OnFrameworkInitializationCompleted()
    {
        switch (ApplicationLifetime)
        {
            case ISingleViewApplicationLifetime single:
                single.MainView = new MobileRoot(new Shell(Program.DataDirectory));
                break;
            case IClassicDesktopStyleApplicationLifetime desktop:
                var root = new MobileRoot(new Shell(Program.DataDirectory));
                desktop.MainWindow = new Window { Content = root, Width = 412, Height = 900, Title = "Minter Wallet (Android preview)" };
                desktop.MainWindow.Closed += (_, _) => root.Shell.StopTimers();
                break;
        }
        base.OnFrameworkInitializationCompleted();
    }

    // "system" follows the OS; "dark" and "light" are fixed.
    public static void ApplyTheme(string? theme)
    {
        if (Current != null) Current.RequestedThemeVariant = theme switch { "dark" => ThemeVariant.Dark, "light" => ThemeVariant.Light, _ => ThemeVariant.Default };
    }
}
