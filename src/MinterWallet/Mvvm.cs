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
    public async void Execute(object? parameter)
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
        HttpRequestException => $"Источник недоступен: {error.Message}",
        TaskCanceledException => "Источник не ответил вовремя.",
        MinterWallet.Core.ChainApiException api => $"Источник ответил ошибкой {api.Code}: {api.Message}",
        FormatException => "Проверьте число: цифры и точка, не больше 18 знаков после неё.",
        _ => error.Message
    };
}
