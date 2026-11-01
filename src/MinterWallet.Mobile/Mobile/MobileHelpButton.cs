using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace MinterWallet.Mobile;

// A named tap target on phones, with a scrollable explanation that follows the current viewport.
public sealed class MobileHelpButton : Button
{
    protected override Type StyleKeyOverride => typeof(Button);
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<MobileHelpButton, string?>(nameof(Text));
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    public MobileHelpButton()
    {
        Classes.Add("icon");
        MinWidth = MinHeight = 44;
        Content = new Icon { Kind = "help", Width = 20, Height = 20 };
        AutomationProperties.SetName(this, Localization.Text("Показать пояснение"));
        var body = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
        body.Bind(TextBlock.TextProperty, this.GetObservable(TextProperty));
        Flyout = CreateFlyout(this, body);
    }

    public static Flyout For(Button anchor, string text) => CreateFlyout(anchor, new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
    public static Flyout For(Button anchor, Control body)
    {
        body.Bind(DataContextProperty, anchor.GetObservable(DataContextProperty));
        return CreateFlyout(anchor, body);
    }

    private static Flyout CreateFlyout(Button anchor, Control body)
    {
        var scroll = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Width = 280, MaxHeight = 400 };
        var flyout = new Flyout { Content = scroll, Placement = PlacementMode.BottomEdgeAlignedRight };
        flyout.Opened += (_, _) =>
        {
            var size = TopLevel.GetTopLevel(anchor)?.ClientSize ?? new Size(360, 640);
            scroll.Width = Math.Max(160, Math.Min(320, size.Width - 64));
            scroll.MaxHeight = Math.Max(120, size.Height - 160);
        };
        return flyout;
    }
}
