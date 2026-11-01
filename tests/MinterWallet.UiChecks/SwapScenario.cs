using System.Net;
using System.Security.Cryptography;
using System.Text;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Minter.Protocol;
using MinterWallet;
using MinterWallet.Api;
using MinterWallet.Core;
using MinterWallet.Views;

public static class SwapScenario
{
    public static async Task<int> Run()
    {
        var directory = Directory.CreateTempSubdirectory("minterwallet-swap-").FullName;
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui-swap"));
        Directory.CreateDirectory(output);
        MainWindow? window = null;
        Shell? shell = null;
        try
        {
            var (store, session) = Keystore.Create(Keystore.DefaultPath(directory), "test-password", new(8192, 1, 1, RandomNumberGenerator.GetBytes(16)));
            var account = session.AddPrivateKey(Convert.FromHexString(new string('1', 64)), "Обмен");
            new WalletSettings { CheckUpdates = false, Telemetry = false, Language = "ru", Theme = "dark", LastAccountId = account.Id }.Save(directory);
            var handler = new SwapHandler();
            var backend = new NodeApiClient(new Uri("http://wallet.test/"), handler: handler);
            shell = new Shell(directory) { AutoRefresh = false };
            typeof(Shell).GetProperty(nameof(Shell.Backend))!.SetValue(shell, backend);
            typeof(Shell).GetProperty(nameof(Shell.Status))!.SetValue(shell, new ChainStatus("minter-test-network", "test", 100, DateTimeOffset.UtcNow));
            typeof(Shell).GetProperty(nameof(Shell.Wallet))!.SetValue(shell, new WalletService(backend, session, ChainId.Testnet));
            shell.Opened(store, session);
            var main = (MainViewModel)shell.Page;
            main.Section = Section.Swap;
            window = new MainWindow(shell) { Width = 1120, Height = 960 };
            window.Show();
            await Until(() => main.Balances.Count == 1 && main.HasCatalog);
            var swap = main.Swap;
            var page = window.FindDescendantOfType<SwapPage>()!;
            var mode = page.GetVisualDescendants().OfType<ListBox>().Single(b => AutomationProperties.GetName(b) == "Режим обмена");
            mode.SelectedIndex = 1; Pump();
            var amount = page.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Сколько купить");
            var amountCard = amount.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"));
            Check(amountCard.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Получаю"), "buy amount must be entered beside the coin being bought, not the coin being sold");
            var picker = page.GetVisualDescendants().OfType<CoinPicker>().Single(p => ReferenceEquals(p.DataContext, swap.Buy));
            var open = picker.FindControl<Button>("Open")!;
            open.Flyout!.ShowAt(open); Pump();
            var search = picker.FindControl<TextBox>("Search")!;
            search.Text = "WANTED"; Pump();
            Check(swap.Buy.Results is [{ Id: 7 }], "target picker offers a catalog coin that is absent from the balance");
            search.RaiseEvent(new KeyEventArgs { RoutedEvent = InputElement.KeyDownEvent, Key = Key.Enter });
            amount.Text = "10";
            await swap.Estimate.RunAsync(); Pump();
            Check(swap.Buy.Selected?.Id == 7 && swap.Sell?.Coin.Id == 0 && handler.LastQuote!.Contains("/0/7/output/10000000000000000000", StringComparison.Ordinal), "buy mode requests the exact wanted amount from the backend");
            Check(swap.QuoteExpected == "7 BIP" && swap.QuoteLimit.Contains("7.07 BIP", StringComparison.Ordinal), "buy quote and maximum spend use the payment currency");
            Shot(window, output, "buy-dark");
            mode.SelectedIndex = 0; Pump();
            var sellAmount = page.GetVisualDescendants().OfType<TextBox>().Single(b => b.IsEffectivelyVisible && AutomationProperties.GetName(b) == "Сколько продать");
            var sellCard = sellAmount.GetVisualAncestors().OfType<Border>().First(b => b.Classes.Contains("card"));
            Check(sellCard.GetVisualDescendants().OfType<TextBlock>().Any(t => t.Text == "Отдаю") && !swap.HasQuote, "sell mode moves exact input to the payment card and invalidates the buy quote");
            await swap.Estimate.RunAsync(); Pump();
            Check(handler.LastQuote!.Contains("/0/7/input/10000000000000000000", StringComparison.Ordinal) && swap.QuoteExpected == "22 WANTED", "sell mode estimates the received amount in the destination currency");
            Shot(window, output, "sell-dark");
            App.ApplyTheme("light"); mode.SelectedIndex = 1;
            await swap.Estimate.RunAsync(); Pump();
            Shot(window, output, "buy-light");
            var refreshReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.HoldQuote = refreshReady;
            var refreshing = swap.RequoteAsync(); Pump();
            Check(swap.HasQuote && swap.SwapNow.CanExecute(null), "price refresh retains the current quote and action");
            refreshReady.SetResult(); await refreshing;
            var lateReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            handler.HoldQuote = lateReady;
            var late = swap.Estimate.RunAsync();
            mode.SelectedIndex = 0; swap.Stop();
            lateReady.SetResult(); await late;
            Check(!swap.HasQuote && !swap.SwapNow.CanExecute(null), "late buy quote cannot appear in sell mode");
            Check(handler.Broadcasts == 0, "switching modes and estimating never sends a transaction");
            Console.WriteLine("PASS swap UI: buy/sell amount placement, unowned catalog coin selection, exact output/input requests, currency and slippage; no broadcast");
            return 0;
        }
        catch (Exception error) { Console.WriteLine("FAIL swap UI: " + error); return 1; }
        finally { shell?.Lock(); window?.Close(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    private static void Pump() { Dispatcher.UIThread.RunJobs(); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); }
    private static async Task Until(Func<bool> done)
    {
        var end = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < end) { await Task.Delay(20); Pump(); }
        Check(done(), "swap fixture did not load");
    }
    private static void Shot(Window window, string output, string name) { Pump(); window.CaptureRenderedFrame()?.Save(Path.Combine(output, name + ".png"), new Avalonia.Media.Imaging.PngBitmapEncoderOptions()); }

    private sealed class SwapHandler : HttpMessageHandler
    {
        public string? LastQuote { get; private set; }
        public int Broadcasts { get; private set; }
        public TaskCompletionSource? HoldQuote { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            string json;
            if (path.Contains("best_trade", StringComparison.Ordinal))
            {
                LastQuote = path;
                if (HoldQuote is { } held) { HoldQuote = null; await held.Task.WaitAsync(cancellationToken); }
                json = "{\"path\":[0,7],\"result\":\"" + Units.ParseCoins(path.Contains("/output/", StringComparison.Ordinal) ? "7" : "22") + "\"}";
            }
            else if (path.EndsWith("/status", StringComparison.Ordinal)) json = "{\"network\":\"minter-test-network\",\"version\":\"test\",\"latest_block_height\":\"100\",\"latest_block_time\":\"2026-11-01T00:00:00Z\"}";
            else if (path.EndsWith("/coins", StringComparison.Ordinal)) json = "{\"coins\":[{\"id\":0,\"symbol\":\"BIP\",\"name\":\"BIP\",\"type\":\"coin\",\"reserve\":\"0\",\"pool_base\":\"0\",\"pools\":1},{\"id\":7,\"symbol\":\"WANTED\",\"name\":\"Wanted token\",\"type\":\"token\",\"reserve\":\"0\",\"pool_base\":\"0\",\"pools\":1}]}";
            else if (path.Contains("/address/", StringComparison.Ordinal)) json = "{\"balance\":[{\"coin\":{\"id\":0,\"symbol\":\"BIP\"},\"value\":\"100000000000000000000\",\"bip_value\":\"100000000000000000000\"}],\"transaction_count\":\"0\",\"bip_value\":\"100000000000000000000\",\"delegated\":[]}";
            else if (path.Contains("/frozen/", StringComparison.Ordinal)) json = "{\"frozen\":[]}";
            else if (path.EndsWith("transactions", StringComparison.Ordinal)) json = "{\"transactions\":[]}";
            else if (path.Contains("send_transaction", StringComparison.Ordinal)) { Broadcasts++; throw new InvalidOperationException("Swap UI check must never broadcast"); }
            else json = "{}";
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        }
    }
}
