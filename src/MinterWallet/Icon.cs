using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace MinterWallet;

// A line icon on a 24×24 grid, stroked with the inherited text colour: no icon font or image files in the single file.
public sealed class Icon : Control
{
    public static readonly StyledProperty<string?> KindProperty = AvaloniaProperty.Register<Icon, string?>(nameof(Kind));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<Icon>();

    private static readonly Dictionary<string, Geometry> Shapes = new()
    {
        ["wallet"] = Geometry.Parse("M3 7 H19 A2 2 0 0 1 21 9 V18 A2 2 0 0 1 19 20 H5 A2 2 0 0 1 3 18 Z M3 7 V6 A2 2 0 0 1 5 4 H16 M16 13.5 H17"),
        ["send"] = Geometry.Parse("M7 17 L17 7 M9 7 H17 V15"),
        ["receive"] = Geometry.Parse("M17 7 L7 17 M15 17 H7 V9"),
        ["bridge"] = Geometry.Parse("M3 17 H21 M3 9 H21 M5 9 V17 M19 9 V17 M5 9 C8 13 16 13 19 9"),
        ["swap"] = Geometry.Parse("M4 8 H18 M14 4 L18 8 L14 12 M20 16 H6 M10 12 L6 16 L10 20"),
        ["stake"] = Geometry.Parse("M12 3 L21 8 L12 13 L3 8 Z M3 12.5 L12 17.5 L21 12.5 M3 16.5 L12 21.5 L21 16.5"),
        ["history"] = Geometry.Parse("M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M12 7 V12 L15.5 14"),
        ["settings"] = Geometry.Parse("M4 6 H13 M17 6 H20 M15 4 V8 M4 12 H7 M11 12 H20 M9 10 V14 M4 18 H13 M17 18 H20 M15 16 V20"),
        ["lock"] = Geometry.Parse("M6 11 H18 A1 1 0 0 1 19 12 V20 A1 1 0 0 1 18 21 H6 A1 1 0 0 1 5 20 V12 A1 1 0 0 1 6 11 Z M8 11 V7 A4 4 0 0 1 16 7 V11"),
        ["copy"] = Geometry.Parse("M9 9 H19 A1 1 0 0 1 20 10 V20 A1 1 0 0 1 19 21 H9 A1 1 0 0 1 8 20 V10 A1 1 0 0 1 9 9 Z M16 9 V5 A1 1 0 0 0 15 4 H5 A1 1 0 0 0 4 5 V15 A1 1 0 0 0 5 16 H8"),
        ["paste"] = Geometry.Parse("M8 5 H6 A1 1 0 0 0 5 6 V20 A1 1 0 0 0 6 21 H18 A1 1 0 0 0 19 20 V6 A1 1 0 0 0 18 5 H16 M9 3 H15 V7 H9 Z"),
        ["refresh"] = Geometry.Parse("M20 12 A8 8 0 1 1 17.7 6.3 M20 4 V9 H15"),
        ["download"] = Geometry.Parse("M12 4 V15 M7 10 L12 15 L17 10 M5 20 H19"),
        ["coin"] = Geometry.Parse("M21 12 A9 9 0 1 1 3 12 A9 9 0 1 1 21 12 M12 7 V17 M9 9.5 H15 M9 14.5 H15"),
        ["plus"] = Geometry.Parse("M12 5 V19 M5 12 H19"),
        ["check"] = Geometry.Parse("M5 12.5 L10 17.5 L19 7"),
        ["close"] = Geometry.Parse("M6 6 L18 18 M18 6 L6 18"),
        ["back"] = Geometry.Parse("M19 12 H5 M11 6 L5 12 L11 18"),
        ["info"] = Geometry.Parse("M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M12 11 V16.5 M12 7.5 V8"),
        ["alert"] = Geometry.Parse("M12 3.5 L21.5 20 H2.5 Z M12 10 V14 M12 17 V17.5"),
        ["error"] = Geometry.Parse("M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M9 9 L15 15 M15 9 L9 15"),
        ["success"] = Geometry.Parse("M12 3 A9 9 0 1 0 12 21 A9 9 0 1 0 12 3 Z M8 12.5 L11 15.5 L16.5 9.5"),
        ["loader"] = Geometry.Parse("M12 3 A9 9 0 1 1 3 12"),
        ["phrase"] = Geometry.Parse("M7 3 H14 L19 8 V20 A1 1 0 0 1 18 21 H7 A1 1 0 0 1 6 20 V4 A1 1 0 0 1 7 3 Z M14 3 V8 H19 M9.5 13 H15.5 M9.5 17 H15.5"),
        ["key"] = Geometry.Parse("M20 8 A4 4 0 1 1 12 8 A4 4 0 1 1 20 8 Z M13.2 10.8 L4 20 M6.5 17.5 L9 20 M9 15 L11.5 17.5"),
        ["eye"] = Geometry.Parse("M2 12 C5 6 19 6 22 12 C19 18 5 18 2 12 Z M15 12 A3 3 0 1 1 9 12 A3 3 0 1 1 15 12 Z"),
        ["shield"] = Geometry.Parse("M12 3 L20 6 V12 C20 16.5 16.5 19.8 12 21 C7.5 19.8 4 16.5 4 12 V6 Z M8.5 12 L11 14.5 L15.5 9.5"),
        ["search"] = Geometry.Parse("M17 11 A6 6 0 1 1 5 11 A6 6 0 1 1 17 11 Z M15.5 15.5 L20 20"),
        ["chevron-left"] = Geometry.Parse("M15 6 L9 12 L15 18"),
        ["chevron-right"] = Geometry.Parse("M9 6 L15 12 L9 18"),
        ["users"] = Geometry.Parse("M12 8 A3.5 3.5 0 1 1 5 8 A3.5 3.5 0 1 1 12 8 Z M2.5 20 C3 16 5.5 14.5 8.5 14.5 C11.5 14.5 14 16 14.5 20 M15.5 4.8 A3.5 3.5 0 0 1 15.5 11.2 M17 14.8 C19.5 15.3 21 17 21.5 20"),
        ["moon"] = Geometry.Parse("M20 14.5 A8 8 0 1 1 9.5 4 A6.5 6.5 0 0 0 20 14.5 Z"),
    };

    static Icon()
    {
        AffectsRender<Icon>(KindProperty, ForegroundProperty);
        WidthProperty.OverrideDefaultValue<Icon>(18);
        HeightProperty.OverrideDefaultValue<Icon>(18);
    }

    public string? Kind { get => GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }

    public override void Render(DrawingContext context)
    {
        if (Kind == null || Foreground == null || !Shapes.TryGetValue(Kind, out var shape)) return;
        var scale = Math.Min(Bounds.Width, Bounds.Height) / 24;
        var offset = new Point((Bounds.Width - 24 * scale) / 2, (Bounds.Height - 24 * scale) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
            context.DrawGeometry(null, new Pen(Foreground, 1.9, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), shape);
    }
}
