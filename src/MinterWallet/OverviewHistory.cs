using System.Collections.ObjectModel;
using System.Globalization;
using MinterWallet.Core;

namespace MinterWallet;

public sealed partial class MainViewModel
{
    public ObservableCollection<HistoryItem> Recent { get; } = [];
    private string recentText = "Загрузка операций…";
    private int recentContext = -1;
    public string RecentText { get => recentText; private set => Set(ref recentText, value); }
    public Command OpenHistory => new(() => Section = Section.History);
    private async Task LoadRecentAsync(WalletAccount account, WalletService wallet, int context)
    {
        if (recentContext == context) return;
        recentContext = context;
        try
        {
            var cursor = HistoryCursor(wallet, account.Address, 5);
            var entries = await cursor.NextAsync();
            if (context != accountVersion || !ReferenceEquals(shell.Backend, wallet.Backend) || shell.Session?.IsLocked != false) return;
            var items = new List<HistoryItem>();
            var pending = new List<HistoryItem>();
            foreach (var (record, older) in entries.Take(5))
            {
                try
                {
                    DateTimeOffset? timestamp = null;
                    var isEstimated = false;
                    if (record.Tags.TryGetValue("timestamp", out var t) && DateTimeOffset.TryParse(t, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
                    {
                        timestamp = at;
                        HistoryViewModel.BlockTimeCache[record.Height] = at;
                    }
                    else if (HistoryViewModel.BlockTimeCache.TryGetValue(record.Height, out var cached))
                    {
                        timestamp = cached;
                    }
                    else if (shell.Status is { Height: > 0 } st && st.Height >= record.Height)
                    {
                        timestamp = st.BlockTime - TimeSpan.FromSeconds((st.Height - record.Height) * 5);
                        isEstimated = true;
                    }

                    var item = new HistoryItem(Core.History.Decode(record, account.Address, Symbol), TxExplain.Explain(record, account.Address, Symbol), older, Symbol,
                        a => shell.Store?.Accounts.FirstOrDefault(x => x.Address == a)?.Name ?? shell.AddressBook.NameOf(a), account.Address, timestamp, isEstimated);
                    items.Add(item);
                    if (isEstimated || !timestamp.HasValue) pending.Add(item);
                }
                catch (FormatException) { }
            }
            if (Recent.Count != items.Count || !Recent.Zip(items).All(p => p.First.Entry.Hash == p.Second.Entry.Hash && p.First.FromExplorer == p.Second.FromExplorer))
            {
                Recent.Clear();
                foreach (var item in items) Recent.Add(item);
            }
            RecentText = items.Count == 0 ? "Источник не сообщил операций для этого адреса." : "Последние операции выбранного адреса";
            if (pending.Count > 0)
                _ = FetchRecentBlockTimesAsync(wallet, pending, context);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ChainApiException or ObjectDisposedException)
        {
            if (context == accountVersion) RecentText = $"Операции не загружены: {Describe(e)}";
        }
        finally { if (recentContext == context) recentContext = -1; }
    }

    private async Task FetchRecentBlockTimesAsync(WalletService wallet, List<HistoryItem> pending, int context)
    {
        var distinctHeights = pending.Select(i => i.Entry.Height).Distinct().ToList();
        foreach (var height in distinctHeights)
        {
            if (context != accountVersion || !ReferenceEquals(shell.Backend, wallet.Backend)) return;
            if (HistoryViewModel.BlockTimeCache.TryGetValue(height, out var time))
            {
                foreach (var item in pending.Where(i => i.Entry.Height == height)) item.SetExactTimestamp(time);
                continue;
            }
            try
            {
                time = await wallet.Backend.BlockTimeAsync(height);
                HistoryViewModel.BlockTimeCache[height] = time;
                foreach (var item in pending.Where(i => i.Entry.Height == height)) item.SetExactTimestamp(time);
            }
            catch (Exception e) when (e is HttpRequestException or TaskCanceledException or ChainApiException) { }
            await Task.Delay(40);
        }
    }
}
