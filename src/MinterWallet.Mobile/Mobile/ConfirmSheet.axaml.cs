using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.Input.TextInput;
using MinterWallet.Core;

namespace MinterWallet.Mobile;

// Shows what will be signed and takes the password or fingerprint; gives it (or null) to the caller, which checks it.
public partial class ConfirmSheet : UserControl
{
    private TaskCompletionSource<string?>? pending;
    private string currentTitle = "";
    public Shell? Shell { get; set; }
    private int generation;
    private bool authenticating;
    private object? requestPage;
    private Keystore? requestStore;
    private bool CanUseBiometrics => Shell?.Settings.MobileBiometricsEnabled == true && MobileHost.Platform is { CanUseBiometrics: true, HasBiometricSecret: true };

    public ConfirmSheet()
    {
        InitializeComponent();
        SizeChanged += (_, e) => Card.MaxHeight = Math.Max(240, e.NewSize.Height - 24);
    }

    public Task<string?> AskAsync(ConfirmRequest request)
    {
        Cancel();
        requestPage = Shell?.Page; requestStore = Shell?.Store;
        pending = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        currentTitle = request.Title;
        TitleText.Text = request.Title;
        ConfirmButton.Content = request.Action;
        Warnings.ItemsSource = request.Warnings;
        Rows.Children.Clear();
        for (var i = 0; i < request.Rows.Count; i++)
        {
            var label = new TextBlock { Text = request.Rows[i].Label, Classes = { "caption" }, VerticalAlignment = VerticalAlignment.Center };
            var valueText = request.Rows[i].Value;
            var text = new SelectableTextBlock { Classes = { "mono" }, FontSize = 13, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
            AddressMarks.SetText(text, valueText);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("105,*"), ColumnSpacing = 8 };
            Grid.SetColumn(text, 1);
            grid.Children.Add(label);
            grid.Children.Add(text);
            var row = new Border
            {
                BorderThickness = i == 0 ? new Thickness(0) : new Thickness(0, 1, 0, 0), Padding = new(0, 7),
                Child = grid,
            };
            row.Bind(Border.BorderBrushProperty, row.GetResourceObservable("Line"));
            Rows.Children.Add(row);
        }
        PasswordBox.Text = "";
        BioButton.IsVisible = CanUseBiometrics;
        PasswordBox.PlaceholderText = Localization.Text(Shell?.Credentials.Label ?? "Пароль");
        TextInputOptions.SetContentType(PasswordBox, Shell?.Credentials.InputType ?? TextInputContentType.Password);
        FallbackButton.Content = Localization.Text(Shell?.Credentials.Label ?? "Пароль");
        AuthStatus.Text = "";
        IsVisible = true;
        PasswordArea.IsVisible = !CanUseBiometrics;
        BiometricArea.IsVisible = CanUseBiometrics;
        if (CanUseBiometrics)
        {
            var version = generation;
            Dispatcher.UIThread.Post(() => { if (Current(version)) OnBiometric(this, new RoutedEventArgs()); }, DispatcherPriority.Background);
        }
        else PasswordBox.Focus();
        return pending.Task;
    }

    public void Cancel() => Close(null);

    private bool Current(int version) => version == generation && pending != null && IsVisible
        && ReferenceEquals(Shell?.Page, requestPage) && ReferenceEquals(Shell?.Store, requestStore);

    private void Close(string? password)
    {
        generation++;
        MobileHost.Platform?.CancelBiometrics();
        authenticating = false;
        IsVisible = false;
        PasswordBox.Text = "";
        var done = pending;
        pending = null;
        done?.TrySetResult(password);
    }

    private async void OnConfirm(object? sender, RoutedEventArgs e)
    {
        var version = generation;
        if (authenticating || !Current(version)) return;
        if (string.IsNullOrEmpty(PasswordBox.Text))
        {
            if (CanUseBiometrics && BiometricArea.IsVisible) OnBiometric(sender, e);
            else PasswordBox.Focus();
            return;
        }
        authenticating = true;
        try
        {
            var secret = Shell == null ? PasswordBox.Text : await Shell.Credentials.VerifyAsync(PasswordBox.Text);
            if (Current(version)) Close(secret);
        }
        catch (Exception error)
        {
            if (Current(version)) AuthStatus.Text = Localization.Text(error is WrongPasswordException ? Shell!.Credentials.Incorrect : error.Message);
        }
        finally { if (version == generation) authenticating = false; }
    }

    private void OnFallback(object? sender, RoutedEventArgs e)
    {
        MobileHost.Platform?.CancelBiometrics();
        BiometricArea.IsVisible = false;
        PasswordArea.IsVisible = true;
        PasswordBox.Focus();
    }

    private async void OnBiometric(object? sender, RoutedEventArgs e)
    {
        var version = generation;
        if (authenticating || !Current(version) || !CanUseBiometrics || MobileHost.Platform is not { } platform) return;
        authenticating = true;
        try
        {
            var secret = await platform.ReadBiometricSecretAsync(Localization.Text(currentTitle), Localization.Text("Подтвердите операцию биометрией"));
            if (!Current(version)) return;
            if (!string.IsNullOrEmpty(secret))
            {
                // Verify the OS-decrypted secret against this exact wallet, including after a password change.
                if (requestStore != null)
                {
                    using var session = await Task.Run(() => requestStore.Unlock(secret));
                    if (!Current(version)) return;
                    Shell?.Credentials.ResetAttempts();
                }
                Close(secret);
            }
            else
            {
                AuthStatus.Text = Localization.Text(platform.BiometricError);
                OnFallback(sender, e);
            }
        }
        catch
        {
            if (Current(version))
            {
                AuthStatus.Text = Localization.Text("Введите PIN-код или пароль и включите биометрию заново в настройках.");
                platform.ClearBiometricSecret();
                OnFallback(sender, e);
            }
        }
        finally { if (version == generation) authenticating = false; }
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Cancel();

    private void OnBackdrop(object? sender, PointerPressedEventArgs e) => Cancel();
}
