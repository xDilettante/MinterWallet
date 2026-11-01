using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Styling;

namespace MinterWallet.Mobile;

public partial class MobileStyles : Styles
{
    public MobileStyles()
    {
        AvaloniaXamlLoader.Load(this);
        // Icons the phone's bars need beyond the desktop's set (same 24×24 stroked grid).
        Icon.Shapes.TryAdd("menu", Geometry.Parse("M4 7 H20 M4 12 H20 M4 17 H20"));
    }
}
