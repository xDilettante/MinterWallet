using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace MinterWallet;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(name);
        return true;
    }
}

public sealed class Command(Action run, Func<bool>? canRun = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canRun?.Invoke() ?? true;
    public void Execute(object? parameter) => run();
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

// An async action that cannot run twice at once; errors go to the owner's status line instead of crashing the UI.
public sealed class AsyncCommand(Func<Task> run, Action<Exception> onError, Func<bool>? canRun = null) : ICommand
{
    private bool running;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !running && (canRun?.Invoke() ?? true);
    public async void Execute(object? parameter) => await RunAsync();
    // The same, for a caller that waits for it (nothing happens while it already runs).
    public async Task RunAsync()
    {
        if (running) return;
        running = true; Refresh();
        try { await run(); }
        catch (Exception error) { onError(error); }
        finally { running = false; Refresh(); }
    }
    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed class RelayCommand(Action<object?> run) : ICommand
{
    public event EventHandler? CanExecuteChanged { add { } remove { } }
    public bool CanExecute(object? parameter) => true;
    public void Execute(object? parameter) => run(parameter);
}

public enum Tone { None, Info, Progress, Success, Warning, Error }

// A screen with one status line; the view shows it as a notice coloured by its tone.
public abstract class ScreenModel : Observable
{
    private string status = "";
    private Tone tone;

    public string Status { get => status; private set => Set(ref status, value); }
    public Tone Tone { get => tone; private set => Set(ref tone, value); }

    public void Say(string text, Tone tone = Tone.Info)
    {
        Tone = text.Length == 0 ? Tone.None : tone;
        Status = text;
    }

    public void Fail(Exception error) => Say(Describe(error), Tone.Error);

    public static string Describe(Exception error) => error switch
    {
        HttpRequestException => $"Источник недоступен: {Cause(error)}",
        TaskCanceledException => "Источник не ответил вовремя. Возможно, порт источника закрыт в вашей сети или мешает VPN или прокси.",
        MinterWallet.Core.ChainApiException api => $"Источник ответил ошибкой {api.Code}: {api.Message}",
        FormatException => "Проверьте число: цифры и точка, не больше 18 знаков после неё.",
        _ => error.Message
    };

    // What actually went wrong under "The SSL connection could not be established" and the like, with what to do.
    private static string Cause(Exception error)
    {
        var inner = error;
        while (inner.InnerException != null) inner = inner.InnerException;
        return inner switch
        {
            System.Security.Authentication.AuthenticationException a when a.Message.Contains("NotTimeValid", StringComparison.Ordinal) =>
                "сертификат источника не подходит по дате. Проверьте дату, время и часовой пояс на компьютере.",
            System.Security.Authentication.AuthenticationException a when a.Message.Contains("UntrustedRoot", StringComparison.Ordinal) || a.Message.Contains("PartialChain", StringComparison.Ordinal) =>
                "система не доверяет сертификату источника. Обновите корневые сертификаты (Центр обновления Windows) или отключите проверку HTTPS в антивирусе для кошелька.",
            System.Security.Authentication.AuthenticationException =>
                $"защищённое соединение не установлено ({inner.Message}). Проверьте дату и время на компьютере, антивирус с проверкой HTTPS, VPN или прокси.",
            System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.ConnectionRefused } =>
                "соединение отклонено. Если включён системный прокси или VPN-клиент — проверьте, что он работает, или отключите его.",
            System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.TimedOut or System.Net.Sockets.SocketError.NetworkUnreachable or System.Net.Sockets.SocketError.HostUnreachable } =>
                "нет ответа. Возможно, порт источника закрыт в вашей сети (офис, публичный Wi-Fi) — попробуйте другую сеть или VPN.",
            System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound or System.Net.Sockets.SocketError.NoData } =>
                "адрес источника не найден. Проверьте подключение к интернету и адрес в настройках.",
            _ when !ReferenceEquals(inner, error) => $"{error.Message} ({inner.Message})",
            _ => error.Message
        };
    }
}
