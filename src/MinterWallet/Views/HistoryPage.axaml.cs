using Avalonia;
using Avalonia.Controls;

namespace MinterWallet.Views;

public partial class HistoryPage : UserControl
{
    public HistoryPage()
    {
        InitializeComponent();
        // Date group headers are non-interactive separator items.
        Transactions.ContainerPrepared += (_, e) =>
        {
            if (e.Container is ListBoxItem lbi && Transactions.ItemsSource is System.Collections.IList list && e.Index >= 0 && e.Index < list.Count)
            {
                if (list[e.Index] is DateGroupHeader)
                {
                    lbi.Classes.Add("dateHeaderItem");
                    lbi.Focusable = false;
                    lbi.IsHitTestVisible = false;
                }
                else
                {
                    lbi.Classes.Remove("dateHeaderItem");
                    lbi.Focusable = true;
                    lbi.IsHitTestVisible = true;
                }
            }
        };

        // A click on a transaction opens or closes its details; the list keeps no selection.
        Transactions.SelectionChanged += async (_, _) =>
        {
            if (Transactions.SelectedItem is not HistoryItem item || DataContext is not MainViewModel main) return;
            Transactions.SelectedItem = null;
            await main.History.ToggleAsync(item);
        };
    }
}
