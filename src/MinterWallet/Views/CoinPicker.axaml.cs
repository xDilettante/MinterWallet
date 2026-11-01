using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;

namespace MinterWallet.Views;

public partial class CoinPicker : UserControl
{
    public CoinPicker()
    {
        InitializeComponent();
        // Opening starts a fresh search. A click or Enter picks a coin and closes the list; arrows only move through it.
        Open.Flyout!.Opened += (_, _) => { if (DataContext is CoinPickerViewModel picker) { picker.Query = ""; picker.Refresh(); } Search.Focus(); };
        List.AddHandler(PointerReleasedEvent, (_, _) => { if (List.SelectedItem is CoinOption coin) Pick(coin); }, RoutingStrategies.Bubble, handledEventsToo: true);
        List.KeyDown += (_, e) => { if (e.Key == Key.Enter && List.SelectedItem is CoinOption coin) { Pick(coin); e.Handled = true; } };
        Search.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter && DataContext is CoinPickerViewModel { Results: [var first, ..] }) { Pick(first); e.Handled = true; }
            else if (e.Key == Key.Down && List.ItemCount > 0) { List.SelectedIndex = 0; (List.ContainerFromIndex(0) as Control)?.Focus(); e.Handled = true; }
        };
    }

    private void Pick(CoinOption coin)
    {
        if (DataContext is CoinPickerViewModel picker) picker.Selected = coin;
        List.SelectedItem = null;
        Open.Flyout?.Hide();
    }
}
