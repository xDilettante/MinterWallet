using MinterWallet.Core;

namespace MinterWallet.Mobile;

public sealed class MobileSecurity : ScreenModel
{
    private readonly Shell shell;
    private string offeredSecret = "";
    private object? offeredPage;
    private bool setupVisible;
    public bool SetupVisible { get => setupVisible; private set => Set(ref setupVisible, value); }
    public bool Available => MobileHost.Platform?.CanUseBiometrics == true;
    public bool Enabled => shell.Settings.MobileBiometricsEnabled && MobileHost.Platform?.HasBiometricSecret == true;
    public string StateText => Enabled ? "Биометрия включена для входа и подтверждения операций." : "Биометрия выключена. Используется PIN-код или пароль.";
    public AsyncCommand Enable { get; }
    public Command Disable { get; }
    public Command Later { get; }

    public MobileSecurity(Shell shell)
    {
        this.shell = shell;
        Enable = new AsyncCommand(EnableAsync, Fail);
        Disable = new Command(DisableNow);
        Later = new Command(() => { shell.Settings.MobileBiometricsAsked = true; shell.Settings.Save(shell.DataDirectory); Dismiss(); });
        shell.Credentials.Authenticated += Offer;
        shell.Credentials.Reset += () => { DisableNow(); shell.Settings.MobileBiometricsAsked = false; shell.Settings.Save(shell.DataDirectory); };
    }

    private void Offer(string secret, bool changed)
    {
        if (changed) { DisableNow(); shell.Settings.MobileBiometricsAsked = false; }
        if (shell.Settings.MobileBiometricsAsked || shell.Page is not MainViewModel || !Available) return;
        offeredSecret = secret; offeredPage = shell.Page;
        SetupVisible = true;
    }

    public void Dismiss() { offeredSecret = ""; offeredPage = null; SetupVisible = false; }

    private async Task EnableAsync()
    {
        if (MobileHost.Platform is not { CanUseBiometrics: true } platform || shell.Page is not MainViewModel page)
        { Say("Биометрия недоступна. Настройте её в системе телефона.", Tone.Warning); return; }
        var secret = ReferenceEquals(offeredPage, page) ? offeredSecret : null;
        if (secret == null && shell.Confirm != null)
            secret = await shell.Confirm(new ConfirmRequest("Включить биометрию", [], [], "Продолжить"));
        if (string.IsNullOrEmpty(secret) || !ReferenceEquals(shell.Page, page)) return;
        Say("");
        if (!await platform.EnableBiometricsAsync(secret)) { Say(platform.BiometricError, Tone.Warning); return; }
        if (!ReferenceEquals(shell.Page, page)) { platform.ClearBiometricSecret(); return; }
        try
        {
            shell.Settings.MobileBiometricsEnabled = true;
            shell.Settings.MobileBiometricsAsked = true;
            shell.Settings.Save(shell.DataDirectory);
        }
        catch { shell.Settings.MobileBiometricsEnabled = false; platform.ClearBiometricSecret(); throw; }
        Dismiss(); Notify();
        Say("Биометрия включена.", Tone.Success);
    }

    private void DisableNow()
    {
        MobileHost.Platform?.ClearBiometricSecret();
        shell.Settings.MobileBiometricsEnabled = false;
        shell.Settings.MobileBiometricsAsked = true;
        shell.Settings.Save(shell.DataDirectory);
        Dismiss(); Notify();
    }

    public void Notify() { Changed(nameof(Enabled)); Changed(nameof(StateText)); }
}
