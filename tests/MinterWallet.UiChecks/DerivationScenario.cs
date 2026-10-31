using System.Net;
using System.Security.Cryptography;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Headless;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Minter.Protocol;
using MinterWallet;
using MinterWallet.Api;
using MinterWallet.Core;

public static class DerivationScenario
{
    private const string Phrase = "abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon abandon about";
    public static async Task<int> Run()
    {
        var directory = Directory.CreateTempSubdirectory("minterwallet-derivation-").FullName;
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-derivation"));
        Directory.CreateDirectory(output);
        MainWindow? window = null;
        Shell? shell = null;
        try
        {
            var file = Keystore.DefaultPath(directory);
            var (store, session) = Keystore.Create(file, "test-password", new(8192, 1, 1, RandomNumberGenerator.GetBytes(16)));
            var first = session.AddMnemonic(Phrase, "Мой кошелёк");
            session.AddIndex(first.SecretId!, 1, "Накопления");
            session.AddPrivateKey(Convert.FromHexString(new string('1', 64)), "Отдельный ключ");
            var settings = new WalletSettings { CheckUpdates = false, Telemetry = false, Language = "ru", Theme = "dark", LastAccountId = first.Id };
            settings.Save(directory);
            var handler = new WalletHandler();
            var backend = new NodeApiClient(new Uri("http://wallet.test/"), handler: handler);
            shell = new Shell(directory) { AutoRefresh = false };
            Set(shell, nameof(Shell.Backend), backend);
            Set(shell, nameof(Shell.Status), new ChainStatus("minter-test-network", "test", 100, DateTimeOffset.UtcNow));
            Set(shell, nameof(Shell.Wallet), new WalletService(backend, session, ChainId.Testnet));
            shell.Opened(store, session);
            var main = (MainViewModel)shell.Page;
            window = new MainWindow(shell) { Width = 1200, Height = 900 };
            window.Show();
            var hd = main.Derivation;
            await Wait(() => !hd.Loading && main.State != null);
            Check(hd.Wallets.Count == 2, "sidebar has roots only");
            Check(hd.Cards.Count == 5, "five address cards");
            Check(main.AddressText.Replace(" ", "").Length == 42, "selected address is full");
            var slow = hd.Cards[1].Account.Address.ToString();
            var latest = hd.Cards[2].Account.Address.ToString();
            handler.Nonces[latest] = 2;
            handler.Hold(slow);
            hd.Pick.Execute(hd.Cards[1]);
            await Wait(() => handler.Waiting);
            hd.Pick.Execute(hd.Cards[2]);
            Check(main.State == null && main.Balances.Count == 0, "selection clears previous address state immediately");
            handler.Release();
            await Wait(() => main.State?.Address.ToString() == latest);
            Check(main.State!.Nonce == 2 && main.Selected!.Account.Address.ToString() == latest, "late reply cannot publish previous address state; newest refresh is queued");
            hd.Pick.Execute(hd.Cards[0]);
            await Wait(() => main.State?.Address == first.Address);
            var before = File.ReadAllText(file);
            var selected = main.Selected!.Account;
            hd.Next.Execute(null);
            Check(main.Selected.Account.Address == selected.Address, "paging does not select another address");
            Check(File.ReadAllText(file) == before, "browsing does not add addresses");
            hd.Toggle.Execute(null); hd.Toggle.Execute(null);
            Check(hd.Cards[0].Account.Index == 5, "collapse preserves the carousel page");
            hd.Previous.Execute(null);
            hd.Pick.Execute(hd.Cards[1]);
            Check(main.Selected!.Account.Index == 1 && hd.CurrentName == "Накопления", "card selects address with personal name");
            await Wait(() => main.State != null);
            Shot(window, output, "overview-dark");
            hd.Toggle.Execute(null);
            Check(hd.Collapsed && main.Selected.Account.Index == 1, "collapse keeps selected address");
            Shot(window, output, "collapsed");
            hd.Toggle.Execute(null);
            hd.Edit.Execute(null);
            Check(!main.CanReceive && main.Request.QrText.Length == 0, "editing does not expose an old receive address");
            Check(!main.CanSign, "unapplied path editor disables signing");
            Check(hd.Fields.Count == 5 && hd.Fields.All(f => f.Help.Length > 0), "all path fields have help");
            hd.Fields[2].Value = "7";
            hd.Apply.Execute(null);
            Check(main.Selected.Account.Path == "m/44'/60'/7'/0/1", "individual coordinate controls full derivation");
            Check(hd.NeedsProtection && main.AddressText.Length == 0 && main.Request.QrText.Length == 0 && !main.CanReceive, "risky address hidden across receive surfaces");
            string? copied = null;
            shell.CopyText = value => { copied = value; return Task.CompletedTask; };
            await main.CopyAddress.RunAsync();
            Check(copied == null, "risk blocks clipboard");
            shell.SaveText = (_, _) => Task.FromResult(false);
            await hd.Export.RunAsync();
            hd.Acknowledged = true; hd.ConfirmRisk.Execute(null);
            Check(!hd.BackedUp && !main.CanReceive, "cancelled export cannot unlock receive");
            string? profile = null;
            shell.SaveText = (_, text) => { profile = text; return Task.FromResult(true); };
            await hd.Export.RunAsync();
            Check(profile != null && !profile.Contains(Phrase), "profile contains no mnemonic");
            Check(!main.CanReceive, "saved profile still needs explicit acknowledgement");
            hd.Acknowledged = true; hd.ConfirmRisk.Execute(null);
            Check(main.CanReceive, "successful export plus acknowledgement unlock exact path");
            await main.CopyAddress.RunAsync();
            Check(copied == main.Selected.Account.Address.ToString(), "clipboard is exact selected address");
            hd.Rename.Execute(null); hd.RenameTo = "Работа"; hd.SaveName.Execute(null);
            Check(hd.CurrentName == "Работа", "renaming works for a virtual path");
            shell.OpenText = () => Task.FromResult(profile);
            await hd.Import.RunAsync();
            Check(hd.Tone == Tone.Success, "profile imported after validating all paths");
            hd.Edit.Execute(null); hd.FullPath = "m/44'/60'/7'/0/2"; hd.ApplyFullPath.Execute(null);
            Check(!main.CanReceive && !hd.BackedUp, "approval does not transfer to next path");
            hd.Reset.Execute(null);
            await Wait(() => main.State != null);
            hd.Edit.Execute(null); Shot(window, output, "parameters"); hd.Edit.Execute(null);
            App.ApplyTheme("light"); Shot(window, output, "overview-light");
            main.Section = Section.Accounts; Shot(window, output, "wallets");
            main.Section = Section.Overview;
            hd.SelectedWallet = hd.Wallets.Single(w => !w.Phrase);
            Check(!hd.IsHd && main.CanReceive && hd.Cards.Count == 0, "private key has no child controls");
            main.LoadAccounts(first.Id);
            await Wait(() => main.State != null);
            await Wait(() => !hd.Loading);
            Set(shell, nameof(Shell.GateIndexFrom), 100L);
            shell.Settings.HistoryFromExplorer = false;
            hd.SourceChanged();
            await hd.Scan.RunAsync();
            Check(hd.ScanText.Contains("неполный", StringComparison.Ordinal) && hd.Cards.Any(c => c.Activity == "история неполная"), "partial gate history is not proof of empty addresses");
            handler.Hold(first.Address.ToString());
            var pendingScan = hd.Scan.RunAsync();
            await Wait(() => handler.Waiting);
            hd.SelectedWallet = hd.Wallets.Single(w => !w.Phrase);
            handler.Release();
            await pendingScan;
            Check(!hd.IsHd && hd.Summary.Length == 0 && !hd.Loading && hd.Cards.Count == 0, "cancelled scan cannot publish into another wallet");
            main.LoadAccounts(first.Id);
            await Wait(() => main.State != null && !hd.Loading);
            Shot(window, output, "partial-history");
            main.Settings.LanguageIndex = 2;
            Dispatcher.UIThread.RunJobs();
            hd.Edit.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var personalNames = new HashSet<string> { "Мой кошелёк", "Отдельный ключ", "Накопления", "Работа" };
            var untranslated = window.GetVisualDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible)
                .Select(b => b.Inlines is { Count: > 0 } ? string.Concat(b.Inlines.OfType<Run>().Select(r => r.Text)) : b.Text ?? "")
                .Where(t => Localization.HasCyrillic(t) && !personalNames.Contains(t)).ToList();
            Check(untranslated.Count == 0, "English derivation UI: " + string.Join("; ", untranslated));
            Check(hd.Fields.All(f => !Localization.HasCyrillic(Localization.Text(f.Help))), "parameter helpers translated");
            Shot(window, output, "parameters-english");
            main.Section = Section.Settings;
            Dispatcher.UIThread.RunJobs();
            untranslated = window.GetVisualDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible)
                .Select(b => b.Inlines is { Count: > 0 } ? string.Concat(b.Inlines.OfType<Run>().Select(r => r.Text)) : b.Text ?? "")
                .Where(t => Localization.HasCyrillic(t) && !personalNames.Contains(t) && t != "Русский").ToList();
            Check(untranslated.Count == 0, "English telemetry settings: " + string.Join("; ", untranslated));
            Shot(window, output, "telemetry-settings-english");
            shell.Lock();
            Dispatcher.UIThread.RunJobs();
            var lockTexts = window.GetVisualDescendants().OfType<TextBlock>().Where(b => b.IsEffectivelyVisible).Select(b => b.Text ?? "").ToList();
            Check(lockTexts.All(t => !personalNames.Any(t.Contains) && !t.Contains("Accounts:", StringComparison.Ordinal) && !t.Contains("Счета:", StringComparison.Ordinal)),
                "lock screen hides account names");
            Console.WriteLine("PASS derivation UI: roots, carousel, path fields, names, profile, receive protection, private key and both themes");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL derivation UI: " + e); return 1; }
        finally { window?.Close(); shell?.Lock(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
    private static void Set(Shell shell, string name, object value) => typeof(Shell).GetProperty(name)!.SetValue(shell, value);
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Wait(Func<bool> condition)
    {
        var until = DateTime.UtcNow.AddSeconds(15);
        while (!condition() && DateTime.UtcNow < until) { await Task.Delay(20); Dispatcher.UIThread.RunJobs(); }
        Check(condition(), "async operation timed out");
    }
    private static void Shot(Window window, string output, string name)
    {
        Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()?.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions());
    }
    private sealed class WalletHandler : HttpMessageHandler
    {
        public Dictionary<string, ulong> Nonces { get; } = [];
        private string? held;
        private TaskCompletionSource? release;
        public bool Waiting { get; private set; }
        public void Hold(string address) { held = address; Waiting = false; release = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void Release() { held = null; release?.TrySetResult(); }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await Task.Delay(2, cancellationToken);
            var path = request.RequestUri!.AbsolutePath;
            var address = path[(path.LastIndexOf('/') + 1)..];
            if (path.Contains("/address/", StringComparison.Ordinal) && held == address && release != null) { Waiting = true; await release.Task.WaitAsync(cancellationToken); }
            var json = path.EndsWith("status", StringComparison.Ordinal) ? "{\"network\":\"minter-test-network\",\"version\":\"test\",\"latest_block_height\":\"100\",\"latest_block_time\":\"2026-10-29T00:00:00Z\"}"
                : path.Contains("/address/", StringComparison.Ordinal) ? "{\"balance\":[],\"transaction_count\":\"" + Nonces.GetValueOrDefault(address) + "\",\"bip_value\":\"0\",\"delegated\":[]}"
                : path.Contains("/frozen/", StringComparison.Ordinal) ? "{\"frozen\":[]}"
                : path.EndsWith("transactions", StringComparison.Ordinal) ? "{\"transactions\":[]}"
                : "{}";
            return new(HttpStatusCode.OK) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
        }
    }
}
