using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace MinterWallet.Mobile;

public partial class MobileUnlockView : UserControl
{
    public MobileUnlockView() => InitializeComponent();

    private async void OnBiometric(object? sender, RoutedEventArgs e)
    {
        if (DataContext is UnlockViewModel unlock && this.FindAncestorOfType<MobileRoot>() is { } root)
            await root.PromptBiometricUnlockAsync(unlock);
    }
}
