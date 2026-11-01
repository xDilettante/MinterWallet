using Avalonia.Controls;
using Avalonia.Input;

namespace MinterWallet.Views;

public partial class OverviewPage : UserControl
{
    public OverviewPage()
    {
        InitializeComponent();
        KeyDown += (_, e) =>
        {
            if (DataContext is not MainViewModel main || !main.Derivation.Renaming) return;
            if (e.Key == Key.Escape) { main.Derivation.CancelName.Execute(null); e.Handled = true; }
            if (e.Key == Key.Enter && e.Source is TextBox) { main.Derivation.SaveName.Execute(null); e.Handled = true; }
        };
    }
}
