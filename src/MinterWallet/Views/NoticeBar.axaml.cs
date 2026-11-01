using Avalonia;
using Avalonia.Controls;

namespace MinterWallet.Views;

// The status line of a screen: hidden while empty, coloured by tone, closable until the next message.
public partial class NoticeBar : UserControl
{
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<NoticeBar, string?>(nameof(Text));
    public static readonly StyledProperty<Tone> ToneProperty = AvaloniaProperty.Register<NoticeBar, Tone>(nameof(Tone));

    public NoticeBar()
    {
        InitializeComponent();
        Close.Click += (_, _) => IsVisible = false;
        Update();
    }

    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public Tone Tone { get => GetValue(ToneProperty); set => SetValue(ToneProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == TextProperty || change.Property == ToneProperty) Update();
    }

    private void Update()
    {
        if (Message == null) return;
        Message.Text = Text;
        IsVisible = !string.IsNullOrEmpty(Text);
        Classes.Set("success", Tone == Tone.Success);
        Classes.Set("warning", Tone == Tone.Warning);
        Classes.Set("error", Tone == Tone.Error);
        Classes.Set("progress", Tone == Tone.Progress);
        Glyph.Kind = Tone switch { Tone.Success => "success", Tone.Warning => "alert", Tone.Error => "error", Tone.Progress => "loader", _ => "info" };
        Glyph.Classes.Set("spin", Tone == Tone.Progress);
    }
}
