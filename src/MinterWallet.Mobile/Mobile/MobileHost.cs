namespace MinterWallet;

// What the shared code asks of the entry point (the desktop has its own Program.cs). A phone runs one wallet in its
// own private storage: the platform head sets DataDirectory before the app starts (Android: Context.FilesDir).
internal static class Program
{
    public static string Version => typeof(Program).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
    public static string? DataDirectory { get; set; }
}

// Set by the platform head: the services only the phone's OS gives (null in the checks and the desktop preview).
public interface IMobilePlatform
{
    // A notification of the system (coins arrived).
    void Notify(string title, string text);
    // Keeps the OS from capturing the screen (recents thumbnails, screenshots) while secrets may be shown.
    void SetSecure(bool secure);
    bool ShowsClipboardConfirmation => false;
    // Hardware-backed biometric unlock & signing (Android KeyStore + BiometricPrompt).
    bool CanUseBiometrics => false;
    bool HasBiometricSecret => false;
    bool CanUsePin => false;
    string DerivePinCredential(string pin) => throw new NotSupportedException();
    Task<bool> EnableBiometricsAsync(string verifiedSecret) => Task.FromResult(false);
    void ClearBiometricSecret() { }
    void CancelBiometrics() { }
    string BiometricError => "Биометрия недоступна. Введите PIN-код или пароль.";
    Task<string?> ReadBiometricSecretAsync(string title, string subtitle) => Task.FromResult<string?>(null);
}

public static class MobileHost
{
    public static string? DataDirectory { get => Program.DataDirectory; set => Program.DataDirectory = value; }
    private static IMobilePlatform? platform;
    public static event Action? PlatformChanged;
    public static IMobilePlatform? Platform
    {
        get => platform;
        set { if (ReferenceEquals(platform, value)) return; platform = value; PlatformChanged?.Invoke(); }
    }
}
