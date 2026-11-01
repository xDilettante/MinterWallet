using Avalonia.Controls;
using Avalonia.Input;

namespace MinterWallet.Mobile;

public partial class MobileReceiveView : UserControl
{
    public MobileReceiveView()
    {
        InitializeComponent();
        SizeChanged += (_, e) => ReceiveCard.MaxHeight = Math.Max(180, e.NewSize.Height - 16);
    }

    private void OnBackdrop(object? sender, PointerPressedEventArgs e)
    {
        if (ReferenceEquals(e.Source, sender) && DataContext is MainViewModel main) main.ShowReceive = false;
    }
}
