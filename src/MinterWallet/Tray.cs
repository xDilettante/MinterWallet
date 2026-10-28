using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using MinterWallet.Core;

namespace MinterWallet;

// The wallet's icon in the tray while it runs: a click or «Открыть» brings the window back, «Заблокировать» locks the
// keys, «Выйти» quits. With MinimizeToTray a minimized window hides into the icon; with CloseToTray the window's close
// button hides it too and the wallet keeps running (the auto-lock as well). Only for the desktop app, not the checks.
public sealed class TrayHost
{
    private readonly IClassicDesktopStyleApplicationLifetime desktop;
    private readonly Window window;
    private readonly Shell shell;
    private readonly NativeMenuItem open, lockNow, quit;
    private bool quitting;

    public TrayHost(Application app, IClassicDesktopStyleApplicationLifetime desktop, Window window, Shell shell)
    {
        this.desktop = desktop; this.window = window; this.shell = shell;
        open = new NativeMenuItem { Command = new Command(Show) };
        lockNow = new NativeMenuItem { Command = new Command(() => { if (shell.Session != null) shell.Lock(); }) };
        quit = new NativeMenuItem { Command = new Command(Quit) };
        var menu = new NativeMenu();
        menu.Items.Add(open); menu.Items.Add(lockNow); menu.Items.Add(new NativeMenuItemSeparator()); menu.Items.Add(quit);
        var icon = new TrayIcon { Icon = window.Icon, Menu = menu };
        icon.Clicked += (_, _) => Show();
        TrayIcon.SetIcons(app, [icon]);
        Retitle();
        shell.Redrawn += Retitle;
        shell.Arrived += arrival => { var (title, text) = Shell.Describe(arrival); Notifier.Show(title, text); };
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && window.WindowState == WindowState.Minimized && shell.Settings.MinimizeToTray) window.Hide();
        };
        window.Closing += (_, e) =>
        {
            if (quitting || !shell.Settings.CloseToTray || e.CloseReason != WindowCloseReason.WindowClosing) return;
            e.Cancel = true;
            window.Hide();
        };
        void Retitle()
        {
            open.Header = Localization.Text("Открыть"); lockNow.Header = Localization.Text("Заблокировать"); quit.Header = Localization.Text("Выйти");
            icon.ToolTipText = Localization.Text("Minter Wallet — работает");
        }
    }

    public void Show()
    {
        window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
    }

    private void Quit()
    {
        quitting = true;
        desktop.Shutdown();
    }
}

// One wallet per data directory: a second launch asks the running one to show its window and leaves — two processes
// writing one key store would not do. A named mutex says whether one runs (an abandoned one is taken over), a named pipe
// carries the "show" from the second launch.
public sealed class SingleInstance
{
    private readonly Mutex mutex;
    private readonly string name;

    private SingleInstance(Mutex mutex, string name) { this.mutex = mutex; this.name = name; }

    public static SingleInstance? Claim(string? dataDirectory)
    {
        var path = Path.GetFullPath(WalletPaths.DataDirectory(dataDirectory));
        var name = "MinterWallet-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(OperatingSystem.IsWindows() ? path.ToLowerInvariant() : path)))[..20];
        var mutex = new Mutex(false, name);
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; }
        if (owned) return new SingleInstance(mutex, name);
        for (var attempt = 0; attempt < 5 && !Signal(name); attempt++) Thread.Sleep(400);
        mutex.Dispose();
        return null;
    }

    // Each connection to the pipe is one "show".
    public void Listen(Action show) => _ = Task.Run(async () =>
    {
        while (true)
        {
            try
            {
                await using var server = new NamedPipeServerStream(name, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await server.WaitForConnectionAsync();
                Dispatcher.UIThread.Post(show);
            }
            catch (IOException) { await Task.Delay(500); }
        }
    });

    private static bool Signal(string name)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", name, PipeDirection.Out);
            client.Connect(1500);
            return true;
        }
        catch (Exception e) when (e is TimeoutException or IOException) { return false; }
    }

    public void KeepAlive() => GC.KeepAlive(mutex);
}
