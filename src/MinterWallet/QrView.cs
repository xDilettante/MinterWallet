using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using QRCoder;

namespace MinterWallet;

// A QR code of a text (the address to receive at): black modules on white with the quiet zone, whole pixels per module
// so phone cameras read it from the screen. Error correction M.
public sealed class QrView : Control
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<QrView, string?>(nameof(Text));
    private bool[,]? modules;

    static QrView() => AffectsRender<QrView>(TextProperty);

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty) modules = Encode(Text);
    }

    private static bool[,]? Encode(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var rows = data.ModuleMatrix;
        var result = new bool[rows.Count, rows.Count];
        for (var y = 0; y < rows.Count; y++)
            for (var x = 0; x < rows.Count; x++) result[y, x] = rows[y][x];
        return result;
    }

    public override void Render(DrawingContext context)
    {
        var size = Math.Min(Bounds.Width, Bounds.Height);
        context.FillRectangle(Brushes.White, new Rect(0, 0, size, size), 8);
        if (modules == null) return;
        var n = modules.GetLength(0);
        var cell = Math.Max(1, Math.Floor(size / n));
        var offset = Math.Floor((size - cell * n) / 2);
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
                if (modules[y, x]) context.FillRectangle(Brushes.Black, new Rect(offset + x * cell, offset + y * cell, cell, cell));
    }
}
