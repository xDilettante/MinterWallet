using Avalonia;
using Minter.Protocol;

namespace MinterWallet;

internal static class Program
{
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    // --data DIR keeps every file of the wallet in DIR (tests, several wallets side by side).
    public static string? DataDirectory { get; private set; }
    // This process as the one wallet of its data directory (null: another one runs and was asked to show itself).
    public static SingleInstance? Instance { get; private set; }
    public static string? InitialPaymentLink { get; private set; }

    [STAThread]
    public static int Main(string[] args)
    {
        if (args is ["--version"]) { Console.WriteLine($"MinterWallet {Version}"); return 0; }
        if (args is ["--self-test"]) return SelfTest();
        if (args is ["--data", var directory, ..]) DataDirectory = Path.GetFullPath(directory);
        InitialPaymentLink = args.FirstOrDefault(arg => PaymentLinks.Parse(arg) != null);
        if (OperatingSystem.IsWindows() && DataDirectory == null && Environment.ProcessPath is { } exe
            && !string.Equals(Path.GetFileNameWithoutExtension(exe), "dotnet", StringComparison.OrdinalIgnoreCase))
        {
            // Portable, per-user registration; a moved executable refreshes its own handler on launch.
            try
            {
                using var protocol = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Classes\minterwallet");
                protocol.SetValue("", "URL:Minter Wallet payment");
                protocol.SetValue("URL Protocol", "");
                using var command = protocol.CreateSubKey(@"shell\open\command");
                command.SetValue("", $"\"{exe}\" \"%1\"");
            }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException) { }
        }
        Instance = SingleInstance.Claim(DataDirectory, InitialPaymentLink);
        if (Instance == null) return 0;
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        Instance.KeepAlive();
        return 0;
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont().LogToTrace();

    // Runs without a display: proves that a published single file carries the protocol code and its resources
    // (word list, BouncyCastle) on the target OS. Public BIP39 test vector "abandon … about", m/44'/60'/0'/0/0.
    private static int SelfTest()
    {
        const string mnemonic = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
        using var key = HdKey.FromMnemonic(mnemonic);
        var tx = Transaction.Create(1, ChainId.Mainnet, 1, 0, new SendData(0, key.Address, Units.ParseCoins("1")));
        tx.Sign(key);
        var ok = key.Address.ToString() == "Mx9858effd232b4033e47d90003d41ec34ecaeda94" && Transaction.FromHex(tx.ToHex()).Sender() == key.Address;
        Console.WriteLine($"MinterWallet {Version} {System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier} " +
            $".NET {Environment.Version}: address {key.Address}, signed {tx.Hash}: {(ok ? "OK" : "FAILED")}");
        return ok ? 0 : 1;
    }
}
