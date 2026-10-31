using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;

namespace MinterWallet;

public sealed class CoinBadge : Control
{
    public static readonly StyledProperty<string?> SymbolProperty = AvaloniaProperty.Register<CoinBadge, string?>(nameof(Symbol));
    public static readonly StyledProperty<uint?> CoinIdProperty = AvaloniaProperty.Register<CoinBadge, uint?>(nameof(CoinId));
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<CoinBadge, double>(nameof(Size), 32);

    private static readonly IBrush DarkBrush = new SolidColorBrush(Color.Parse("#1E2026"));
    private static readonly IBrush DefaultBrush = new SolidColorBrush(Color.Parse("#4A6CF7"));

    private Bitmap? remoteIcon;
    private uint? requestedId;

    private void EnsureRemoteIcon()
    {
        var id = CoinId;
        if (id == null)
        {
            if (requestedId != null)
            {
                requestedId = null;
                remoteIcon = null;
            }
            return;
        }

        if (requestedId == id) return;
        requestedId = id;
        remoteIcon = null;
        _ = LoadRemoteIconAsync(id.Value);
    }

    private async Task LoadRemoteIconAsync(uint id)
    {
        var url = $"https://explorer-static.minter.network/coins/{id}.png";
        var bmp = await ImageCache.LoadAsync(url);
        if (bmp != null && requestedId == id)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (requestedId == id)
                {
                    remoteIcon = bmp;
                    InvalidateVisual();
                }
            });
        }
    }

    private static readonly (string Root, IBrush Background, string? IconKind, string Monogram)[] Known =
    [
        ("BIP", new SolidColorBrush(Color.Parse("#E01E5A")), "minter", "M"),
        ("MNT", new SolidColorBrush(Color.Parse("#E01E5A")), "minter", "M"),
        ("BTC", new SolidColorBrush(Color.Parse("#F7931A")), "btc", "B"),
        ("ETH", new SolidColorBrush(Color.Parse("#627EEA")), "ethereum", "E"),
        ("WETH", new SolidColorBrush(Color.Parse("#627EEA")), "ethereum", "E"),
        ("USDT", new SolidColorBrush(Color.Parse("#26A17B")), "usdt", "T"),
        ("USDC", new SolidColorBrush(Color.Parse("#2775CA")), "usdc", "U"),
        ("BNB", new SolidColorBrush(Color.Parse("#F3BA2C")), "bsc", "B"),
        ("WBNB", new SolidColorBrush(Color.Parse("#F3BA2C")), "bsc", "B"),
        ("BSC", new SolidColorBrush(Color.Parse("#F3BA2C")), "bsc", "B"),
        ("TON", new SolidColorBrush(Color.Parse("#0088CC")), "ton", "T"),
        ("DOGE", new SolidColorBrush(Color.Parse("#C2A633")), "doge", "D"),
        ("DOT", new SolidColorBrush(Color.Parse("#E6007A")), "dot", "D"),
        ("HUB", new SolidColorBrush(Color.Parse("#4361EE")), "hub", "H"),
        ("DAI", new SolidColorBrush(Color.Parse("#F5AC37")), "dai", "D"),
        ("SOL", new SolidColorBrush(Color.Parse("#9945FF")), "sol", "S"),
        ("MATIC", new SolidColorBrush(Color.Parse("#8247E5")), null, "M"),
        ("SHIB", new SolidColorBrush(Color.Parse("#FFA409")), null, "S"),
        ("CAKE", new SolidColorBrush(Color.Parse("#D1884F")), null, "C"),
        ("1INCH", new SolidColorBrush(Color.Parse("#1E2836")), null, "1"),
        ("AVAX", new SolidColorBrush(Color.Parse("#E84142")), null, "A"),
        ("NEAR", new SolidColorBrush(Color.Parse("#111111")), null, "N"),
        ("ATOM", new SolidColorBrush(Color.Parse("#2E3148")), null, "A"),
    ];

    private static readonly IBrush[] Palette =
    [
        new SolidColorBrush(Color.Parse("#4A6CF7")),
        new SolidColorBrush(Color.Parse("#0EA5E9")),
        new SolidColorBrush(Color.Parse("#10B981")),
        new SolidColorBrush(Color.Parse("#8B5CF6")),
        new SolidColorBrush(Color.Parse("#EC4899")),
        new SolidColorBrush(Color.Parse("#F59E0B")),
        new SolidColorBrush(Color.Parse("#64748B")),
        new SolidColorBrush(Color.Parse("#14B8A6")),
    ];

    static CoinBadge()
    {
        AffectsRender<CoinBadge>(SymbolProperty, CoinIdProperty, SizeProperty);
        AffectsMeasure<CoinBadge>(SizeProperty);
    }

    public string? Symbol { get => GetValue(SymbolProperty); set => SetValue(SymbolProperty, value); }
    public uint? CoinId { get => GetValue(CoinIdProperty); set => SetValue(CoinIdProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = Size > 0 ? Size : 32;
        return new Size(s, s);
    }


    public static (IBrush Background, string? IconKind, string Monogram) Resolve(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return (DefaultBrush, null, "?");
        var s = raw.Trim().ToUpperInvariant();

        if (s.StartsWith("LP-", StringComparison.Ordinal))
            return (Palette[3], null, "LP");

        var root = s;
        if (s.Length > 3 && s.EndsWith("BSC", StringComparison.Ordinal)) root = s[..^3];
        else if (s.Length > 2 && s.EndsWith('E') && !s.Equals("DOGE", StringComparison.Ordinal) && !s.Equals("CAKE", StringComparison.Ordinal)) root = s[..^1];

        foreach (var k in Known)
            if (string.Equals(root, k.Root, StringComparison.OrdinalIgnoreCase))
                return (k.Background, k.IconKind, k.Monogram);

        var idx = Math.Abs(s.GetHashCode()) % Palette.Length;
        var mono = s.Length <= 2 ? s : s[..1];
        return (Palette[idx], null, mono);
    }

    public override void Render(DrawingContext context)
    {
        var size = Size > 0 ? Size : 32;
        EnsureRemoteIcon();

        if (remoteIcon != null)
        {
            using (context.PushClip(new RoundedRect(new Rect(0, 0, size, size), size / 2)))
            {
                context.DrawImage(remoteIcon, new Rect(0, 0, size, size));
            }
            return;
        }

        var (bg, iconKind, mono) = Resolve(Symbol);
        var center = new Point(size / 2, size / 2);
        var radius = size / 2;

        context.DrawEllipse(bg, null, center, radius, radius);

        var fg = string.Equals(iconKind, "bsc", StringComparison.OrdinalIgnoreCase) ? DarkBrush : Brushes.White;

        if (iconKind != null && Icon.Shapes.TryGetValue(iconKind, out var shape))
        {
            var isFilled = Icon.FilledShapes.Contains(iconKind);
            var target = size * 0.58;
            var scale = target / 24.0;
            var offset = new Point((size - 24.0 * scale) / 2.0, (size - 24.0 * scale) / 2.0);

            using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
            {
                if (isFilled)
                    context.DrawGeometry(fg, null, shape);
                else
                    context.DrawGeometry(null, new Pen(fg, 1.8, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), shape);
            }
        }
        else
        {
            var text = mono.Length > 0 ? mono : "?";
            var fontSize = mono.Length > 1 ? size * 0.36 : size * 0.44;
            var ft = new FormattedText(
                text,
                CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight,
                new Typeface(FontFamily.Default, FontStyle.Normal, FontWeight.SemiBold),
                fontSize,
                fg);
            context.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
        }
    }
}

