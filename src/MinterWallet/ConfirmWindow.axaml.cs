using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;

namespace MinterWallet;

// Shows what will be signed and takes the password; returns it (or null) to the caller, which checks it.
public partial class ConfirmWindow : Window
{
    public ConfirmWindow() => InitializeComponent();

    public ConfirmWindow(ConfirmRequest request) : this()
    {
        Title = Localization.Text(request.Title);
        TitleText.Text = request.Title;
        ConfirmButton.Content = request.Action;
        Warnings.ItemsSource = request.Warnings;
        for (var i = 0; i < request.Rows.Count; i++)
        {
            Rows.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var line = i == 0 ? new Thickness(0) : new Thickness(0, 1, 0, 0);
            var label = new Border { BorderThickness = line, Padding = new(0, 10, 12, 10), Child = new TextBlock { Text = request.Rows[i].Label, TextWrapping = TextWrapping.Wrap, Classes = { "muted" } } };
            var value = new Border { BorderThickness = line, Padding = new(0, 10), Child = new SelectableTextBlock { Text = request.Rows[i].Value, TextWrapping = TextWrapping.Wrap, Classes = { "mono" } } };
            label.Bind(Border.BorderBrushProperty, label.GetResourceObservable("Line"));
            value.Bind(Border.BorderBrushProperty, value.GetResourceObservable("Line"));
            Grid.SetRow(label, i); Grid.SetRow(value, i); Grid.SetColumn(value, 1);
            Rows.Children.Add(label); Rows.Children.Add(value);
        }
        Opened += (_, _) => PasswordBox.Focus();
    }

    private void OnConfirm(object? sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(PasswordBox.Text)) { PasswordBox.Focus(); return; }
        var password = PasswordBox.Text;
        PasswordBox.Text = "";
        Close(password);
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close(null);
}
