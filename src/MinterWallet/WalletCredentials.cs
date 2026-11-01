using Avalonia.Input.TextInput;
using MinterWallet.Core;

namespace MinterWallet;

// Desktop passwords remain unchanged. A phone may derive a high-entropy credential from a PIN using its KeyStore.
public sealed class WalletCredentials(Shell shell) : Observable
{
    private Func<string, string>? derivePin;
    public Func<string, string>? DerivePin
    {
        get => derivePin;
        set { if (Set(ref derivePin, value)) Changed(nameof(CanUsePin)); }
    }
    public bool Mobile { get; set; }
    public bool CanUsePin => DerivePin != null;
    public bool BiometricsEnabled => shell.Settings.MobileBiometricsEnabled;
    public bool UsesPin => shell.Store?.CredentialKind == "android-pin-v1";
    public string Label => UsesPin ? "PIN-код" : "Пароль";
    public string Required => UsesPin ? "Введите PIN-код." : "Введите пароль.";
    public string Incorrect => UsesPin ? "Неверный PIN-код." : "Неверный пароль.";
    public TextInputContentType InputType => UsesPin ? TextInputContentType.Pin : TextInputContentType.Password;
    public event Action<string, bool>? Authenticated;
    public event Action? Reset;
    private bool checking;

    public string Resolve(string input, bool pin)
    {
        if (!pin) return input;
        if (PinProblem(input) is { } problem) throw new ArgumentException(problem);
        return DerivePin?.Invoke(input) ?? throw new InvalidOperationException("PIN-код доступен только на исходном устройстве Android. Восстановите кошелёк по фразе или ключу.");
    }

    public async Task<(KeystoreSession Session, string Secret)> UnlockAsync(string input)
    {
        if (checking) throw new InvalidOperationException("Проверка уже выполняется.");
        var settings = shell.Settings;
        if (Mobile && settings.MobileAuthBlockedUntil > DateTimeOffset.UtcNow)
            throw new InvalidOperationException($"Слишком много попыток. Повторите через {Math.Ceiling((settings.MobileAuthBlockedUntil - DateTimeOffset.UtcNow).TotalSeconds)} с.");
        var store = shell.Store ?? throw new InvalidOperationException("Кошелёк закрыт.");
        checking = true;
        try
        {
            var secret = Resolve(input, UsesPin);
            var session = await Task.Run(() => store.Unlock(secret));
            if (!ReferenceEquals(shell.Store, store)) { session.Dispose(); throw new InvalidOperationException("Кошелёк изменился. Повторите вход."); }
            try
            {
                ResetAttempts();
            }
            catch { session.Dispose(); throw; }
            return (session, secret);
        }
        catch (WrongPasswordException)
        {
            if (Mobile)
            {
                settings.MobileAuthFailures = Math.Min(15, settings.MobileAuthFailures + 1);
                if (settings.MobileAuthFailures >= 5)
                    settings.MobileAuthBlockedUntil = DateTimeOffset.UtcNow.AddSeconds(Math.Min(300, 30 * Math.Pow(2, settings.MobileAuthFailures - 5)));
                settings.Save(shell.DataDirectory);
            }
            throw;
        }
        finally { checking = false; }
    }

    public async Task<string> VerifyAsync(string input)
    {
        var (session, secret) = await UnlockAsync(input);
        session.Dispose();
        return secret;
    }

    public void ResetAttempts()
    {
        if (!Mobile) return;
        shell.Settings.MobileAuthFailures = 0;
        shell.Settings.MobileAuthBlockedUntil = default;
        shell.Settings.Save(shell.DataDirectory);
    }

    // Only a successfully created/unlocked/re-encrypted wallet may populate the biometric cache.
    public void Accepted(string secret, bool changed = false)
    {
        if (changed)
            foreach (var name in new[] { nameof(UsesPin), nameof(Label), nameof(Required), nameof(Incorrect), nameof(InputType) }) Changed(name);
        Authenticated?.Invoke(secret, changed);
    }
    public void StartOver() => Reset?.Invoke();
    public static string? PinProblem(string pin) => pin.Length is < 6 or > 12 || pin.Any(c => c is < '0' or > '9') ? "PIN-код — от 6 до 12 цифр." : null;
}

public sealed class CredentialChoice : Observable
{
    private readonly WalletCredentials credentials;
    private bool pin;
    public CredentialChoice(WalletCredentials credentials, bool usePin = false)
    {
        this.credentials = credentials;
        pin = usePin;
        var weak = new WeakReference<CredentialChoice>(this);
        credentials.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(WalletCredentials.CanUsePin) && weak.TryGetTarget(out var choice)) choice.Changed(nameof(CanChoose));
        };
    }
    public bool CanChoose => credentials.CanUsePin;
    public bool UsePin => pin;
    public int ModeIndex
    {
        get => pin ? 0 : 1;
        set
        {
            if (!Set(ref pin, value == 0 && CanChoose)) return;
            Changed(nameof(UsePin)); Changed(nameof(Label)); Changed(nameof(RepeatLabel)); Changed(nameof(InputType)); Changed(nameof(Description));
        }
    }
    public string Label => pin ? "PIN-код" : "Пароль";
    public string RepeatLabel => pin ? "PIN-код ещё раз" : "Пароль ещё раз";
    public string Kind => pin ? "android-pin-v1" : "password";
    public TextInputContentType InputType => pin ? TextInputContentType.Pin : TextInputContentType.Password;
    public string Description => pin
        ? "От 6 до 12 цифр. PIN-код работает только на этом устройстве. Для восстановления на другом телефоне сохраните фразу или приватный ключ."
        : "Пароль шифрует ключи на этом устройстве. Если забудете его, восстановите кошелёк по сохранённой фразе или ключу.";
    public string? Problem(string value, string repeat) => pin ? WalletCredentials.PinProblem(value) ?? (value == repeat ? null : "PIN-коды не совпадают.") : Passwords.Problem(value, repeat);
    public string Hint(string value, string repeat) => pin ? (Problem(value, repeat) ?? "PIN-коды совпадают.") : Passwords.Hint(value, repeat);
    public string Resolve(string input) => credentials.Resolve(input, pin);
}
