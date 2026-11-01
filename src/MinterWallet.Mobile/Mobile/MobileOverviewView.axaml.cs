using Avalonia.Controls;
using Avalonia.Interactivity;

namespace MinterWallet.Mobile;

public partial class MobileOverviewView : UserControl
{
    public MobileOverviewView() => InitializeComponent();
    private void OnBridge(object? sender, RoutedEventArgs e) { if (DataContext is MainViewModel main) main.Section = Section.Bridge; }
    private void OnHistory(object? sender, RoutedEventArgs e) { if (DataContext is MainViewModel main) main.Section = Section.History; }
}
