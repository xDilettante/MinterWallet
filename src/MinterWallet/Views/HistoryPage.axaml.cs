using Avalonia.Controls;

namespace MinterWallet.Views;

public partial class HistoryPage : UserControl
{
    public HistoryPage()
    {
        InitializeComponent();
        // A click on a transaction opens or closes its details; the list keeps no selection.
        Transactions.SelectionChanged += async (_, _) =>
        {
            if (Transactions.SelectedItem is not HistoryItem item || DataContext is not MainViewModel main) return;
            Transactions.SelectedItem = null;
            await main.History.ToggleAsync(item);
        };
    }
}
