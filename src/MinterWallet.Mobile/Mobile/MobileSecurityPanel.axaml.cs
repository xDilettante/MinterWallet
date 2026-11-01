using Avalonia.Controls;

namespace MinterWallet.Mobile;

public partial class MobileSecurityPanel : UserControl
{
    public MobileSecurityPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => (DataContext as MobileSecurity)?.Notify();
    }
}
