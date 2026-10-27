using Avalonia.Controls;

namespace MinterWallet.Views;

public partial class UnlockView : UserControl
{
    public UnlockView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => PasswordBox.Focus();
    }
}
