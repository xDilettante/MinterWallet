using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Minter.Protocol;
using MinterWallet;
using MinterWallet.Api;
using MinterWallet.Core;

public static class DerivationDevnetScenario
{
    public static async Task<int> Run(string url)
    {
        var directory = Directory.CreateTempSubdirectory("minterwallet-hd-devnet-").FullName;
        MainWindow? window = null;
        Shell? shell = null;
        try
        {
            using var api = new NodeApiClient(new Uri(url));
            var status = await api.StatusAsync();
            Check(status.Network == "minter-devnet", "isolated devnet only; refusing to sign elsewhere");
            var (store, session) = Keystore.Create(Keystore.DefaultPath(directory), "test-password", new(8192, 1, 1, RandomNumberGenerator.GetBytes(16)));
            using var unlocked = session;
            var faucet = session.AddPrivateKey(SHA256.HashData(Encoding.UTF8.GetBytes("minterwallet-test-1")), "Test faucet");
            var root = session.AddMnemonic(Mnemonic.Generate(), "HD test");
            var child = session.TrackAddress(session.PreviewAddress(root.SecretId!, "m/49'/60'/2'/1/7"));
            var wallet = new WalletService(api, session, ChainId.Testnet, queue: new SendQueue(api, TimeSpan.FromSeconds(60), TimeSpan.FromMilliseconds(500)));
            var funded = await wallet.SendAsync(faucet, await wallet.PreviewAsync(faucet, new SendData(0, child.Address, 2 * Units.PipPerCoin)));
            Check(funded.Succeeded, "fund custom child: " + funded.Log);
            var settings = new WalletSettings { CheckUpdates = false, Telemetry = false, Language = "ru", LastAccountId = child.Id,
                Sources = [new SourceSettings { Name = "Isolated devnet", Url = url, ExpectedNetwork = status.Network, Kind = "node" }] };
            settings.Save(directory);
            shell = new Shell(directory) { AutoRefresh = false };
            typeof(Shell).GetProperty(nameof(Shell.Backend))!.SetValue(shell, api);
            typeof(Shell).GetProperty(nameof(Shell.Status))!.SetValue(shell, status);
            typeof(Shell).GetProperty(nameof(Shell.Wallet))!.SetValue(shell, wallet);
            shell.Opened(store, session);
            var main = (MainViewModel)shell.Page;
            window = new MainWindow(shell) { Width = 1100, Height = 780 };
            window.Show();
            var until = DateTime.UtcNow.AddSeconds(30);
            while (main.State == null && DateTime.UtcNow < until) await Task.Delay(50);
            Check(main.State?.BalanceOf(0) == 2 * Units.PipPerCoin, "selected child balance loaded");
            Check(!main.CanReceive && main.CanSign, "custom-path funds can be spent while receive remains protected");
            var target = await api.CoinAsync(1) ?? throw new InvalidOperationException("Devnet fixture coin 1 is missing.");
            main.Section = Section.Swap;
            main.Swap.Sell = main.Balances.Single(b => b.Coin.Id == 0);
            main.Swap.Buy.Selected = new CoinOption(target.Id, target.Symbol, target.Name, 0, "");
            main.Swap.Amount = "0.1";
            await main.Swap.Estimate.RunAsync();
            Check(main.Swap.HasQuote && main.Swap.SwapNow.CanExecute(null), "initial quote available");
            var quoteDisappeared = false;
            main.Swap.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(SwapViewModel.HasQuote) && !main.Swap.HasQuote) quoteDisappeared = true; };
            await main.Refresh.RunAsync();
            await main.Swap.RequoteAsync();
            Check(!quoteDisappeared && main.Swap.HasQuote && main.Swap.SwapNow.CanExecute(null), "balance and price refresh keep quote and action in place");
            shell.Confirm = request =>
            {
                Check(request.Rows.Any(row => row.Item2.Contains(child.Path, StringComparison.Ordinal)), "confirmation identifies full source path");
                return Task.FromResult<string?>("test-password");
            };
            var sent = await main.ConfirmAndSendAsync("HD derivation test", new SendData(0, faucet.Address, Units.PipPerCoin), 0, [], [], [], main.Send);
            Check(sent?.Succeeded == true, "send from custom HD path: " + sent?.Log);
            if (sent == null) throw new InvalidOperationException("Send was cancelled.");
            var history = await api.HistoryAsync(child.Address, 1, 10);
            Check(history.Any(r => r.Hash == sent.Hash && History.Decode(r, child.Address, _ => "MNT").Direction == Direction.Outgoing), "network confirms custom child sender");
            Check(Keystore.Load(Keystore.DefaultPath(directory)).Accounts.Single(a => a.Id == child.Id).Path == child.Path, "custom path survives disk reload");
            Console.WriteLine($"PASS HD devnet: funded and spent from {child.Path}, block {sent.Height}; exact path in confirmation; risky receive protected; quote stable across balance and price refresh");
            return 0;
        }
        catch (Exception e) { Console.WriteLine("FAIL HD devnet: " + e); return 1; }
        finally { shell?.Lock(); window?.Close(); try { Directory.Delete(directory, true); } catch (IOException) { } }
    }
    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
