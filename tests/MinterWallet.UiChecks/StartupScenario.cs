using System.Security.Cryptography;
using Avalonia.Controls;
using Minter.Protocol;
using MinterWallet;
using MinterWallet.Core;

public static class StartupScenario
{
    public static async Task<int> Run(string url)
    {
        var data = Directory.CreateTempSubdirectory("wallet-startup-").FullName;
        Shell? shell = null;
        Window? window = null;
        try
        {
            var (store, session) = Keystore.Create(Keystore.DefaultPath(data), "startup-test", new(8192, 1, 1, RandomNumberGenerator.GetBytes(16)));
            using var unlocked = session;
            var first = session.AddMnemonic(Mnemonic.Generate(), "Startup test");
            for (uint i = 1; i < 10; i++) session.AddIndex(first.SecretId!, i, $"Адрес № {i + 1}");
            new WalletSettings
            {
                Sources = [new SourceSettings { Name = "Startup source", Url = url }],
                Telemetry = false, CheckUpdates = false, Language = "ru", LastAccountId = first.Id,
            }.Save(data);
            shell = new Shell(data);
            shell.Opened(store, session);
            var main = (MainViewModel)shell.Page;
            window = new MainWindow(shell) { Width = 1100, Height = 720 };
            window.Show();
            var until = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < until)
            {
                if (main.Tone == Tone.Error) throw new InvalidOperationException(main.Status);
                if (main.State != null && !main.Derivation.Loading && main.Derivation.ScanText.StartsWith("Проверено", StringComparison.Ordinal)) break;
                await Task.Delay(50);
            }
            if (main.State == null || main.Derivation.Loading || !main.Derivation.ScanText.StartsWith("Проверено", StringComparison.Ordinal)) throw new InvalidOperationException("Startup timed out: " + main.Status + " / " + main.Derivation.ScanText);
            if (main.Derivation.Status.Contains("429", StringComparison.Ordinal)) throw new InvalidOperationException(main.Derivation.Status);
            if (!main.Connected) throw new InvalidOperationException("Source is not connected.");
            Console.WriteLine($"PASS startup: ten saved addresses, background polling and recovery scan through {main.SourceName}; {main.Derivation.ScanText}");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL startup: " + e); return 1; }
        finally { shell?.Lock(); window?.Close(); try { Directory.Delete(data, true); } catch (IOException) { } }
    }
}
