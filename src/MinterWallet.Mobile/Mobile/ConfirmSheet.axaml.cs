using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace MinterWallet.Mobile;

// Shows what will be signed and takes the password; gives it (or null) to the caller, which checks it. Rows stack the
// label over the value: a phone has no room for the desktop's two columns.
public partial class ConfirmSheet : UserControl
{
    private TaskCompletionSource<string?>? pending;

    public ConfirmSheet()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Card.MaxHeight = Math.Max(240, e.NewSize.Height - 24);
    }

    public Task<string?> AskAsync(ConfirmRequest request)
    {
        pending?.TrySetResult(null);
        pending = new TaskCompletionSource<string?>();
        TitleText.Text = request.Title;
        ConfirmButton.Content = request.Action;
        Warnings.ItemsSource = request.Warnings;
        Rows.Children.Clear();
        for (var i = 0; i < request.Rows.Count; i++)
        {
            var label = new TextBlock { Text = request.Rows[i].Label, TextWrapping = TextWrapping.Wrap, Classes = { "caption" } };
            var text = new SelectableTextBlock { TextWrapping = TextWrapping.Wrap, Classes = { "mono" } };
            AddressMarks.SetText(text, request.Rows[i].Value);
            var row = new Border
            {
                BorderThickness = i == 0 ? new Thickness(0) : new Thickness(0, 1, 0, 0), Padding = new(0, 10),
                Child = new StackPanel { Spacing = 3, Children = { label, text } },
            };
            row.Bind(Border.BorderBrushProperty, row.GetResourceObservable("Line"));
            Rows.Children.Add(row);
        }
        PasswordBox.Text = "";
        IsVisible = true;
        PasswordBox.Focus();
        return pending.Task;
    }

    public void Cancel() => Close(null);

    private void Close(string? password)
    {
        IsVisible = false;
        PasswordBox.Text = "";
        var done = pending;
        pending = null;
        done?.TrySetResult(password);
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Text)) { PasswordBox.Focus(); return; }
        Close(PasswordBox.Text);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Cancel();

    private void OnBackdrop(object? sender, PointerPressedEventArgs e) => Cancel();
}
