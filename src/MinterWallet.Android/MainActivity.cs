using System.Runtime.InteropServices;
using System.Text;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Hardware.Biometrics;
using Android.OS;
using Android.Runtime;
using Android.Security.Keystore;
using Android.Views;
using Avalonia;
using Avalonia.Android;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;

namespace MinterWallet.Android;

[Application]
public sealed class WalletAndroidApplication : AvaloniaAndroidApplication<App>
{
    public WalletAndroidApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

    public override void OnCreate()
    {
        PreloadAndroidNative();
        MobileHost.DataDirectory = FilesDir?.AbsolutePath;
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("LANG")))
            System.Environment.SetEnvironmentVariable("LANG", Java.Util.Locale.Default?.Language ?? "en");
        base.OnCreate();
    }

    [DllImport("android")]
    private static extern IntPtr AChoreographer_getInstance();

    private static void PreloadAndroidNative()
    {
        try
        {
            _ = AChoreographer_getInstance();
        }
        catch { }
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder) =>
        base.CustomizeAppBuilder(builder).WithInterFont();
}

[Activity(
    Label = "Minter Wallet",
    Theme = "@style/MyTheme.NoActionBar",
    Icon = "@mipmap/ic_launcher",
    RoundIcon = "@mipmap/ic_launcher_round",
    MainLauncher = true,
    Exported = true,
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
[IntentFilter([Intent.ActionView], Categories = [Intent.CategoryDefault, Intent.CategoryBrowsable],
    DataScheme = "minterwallet", DataHost = "pay", DataPathPrefix = "/tx/")]
public sealed class MainActivity : AvaloniaMainActivity, IMobilePlatform
{
    private const string ChannelId = "minter_arrivals";
    private AndroidCredentials? credentials;
    private AndroidCredentials Credentials => credentials ??= new AndroidCredentials(this);
    private int notificationId;


    protected override void OnCreate(Bundle? savedInstanceState)
    {
        MobileHost.DataDirectory ??= FilesDir?.AbsolutePath;
        MobileHost.Platform = this;
        try { Credentials.DiscardLegacySecret(); }
        catch { global::Android.Util.Log.Warn("MinterWallet", "Legacy biometric cache cleanup will be retried at the next launch."); }
        base.OnCreate(savedInstanceState);
        EnsureChannel();
        OpenPayment(Intent);
    }

    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);
        OpenPayment(intent);
    }

    private static void OpenPayment(Intent? intent)
    {
        if (intent?.Action == Intent.ActionView && intent.DataString is { } text) MobileHost.OpenPaymentLink(text);
    }

    public bool CanUseBiometrics => Credentials.Available;
    public bool HasBiometricSecret => Credentials.HasSecret;
    public bool CanUsePin => true;
    public bool ShowsClipboardConfirmation => OperatingSystem.IsAndroidVersionAtLeast(33);
    public bool CanScanQr => PackageManager?.HasSystemFeature(global::Android.Content.PM.PackageManager.FeatureCameraAny) == true;
    public Task<string?> ScanQrAsync() => QrScanActivity.ScanAsync(this);
    public void CancelQrScan() => QrScanActivity.Cancel();
    public string BiometricError => Credentials.Error;
    public string DerivePinCredential(string pin) => Credentials.DerivePin(pin);
    public Task<bool> EnableBiometricsAsync(string verifiedSecret) => Credentials.EnableAsync(verifiedSecret);
    public Task<string?> ReadBiometricSecretAsync(string title, string subtitle) => Credentials.ReadAsync(title, subtitle);
    public void ClearBiometricSecret() => Credentials.Clear();
    public void CancelBiometrics() => Credentials.Cancel();

    protected override void OnStop()
    {
        Credentials.Cancel();
        base.OnStop();
    }

    public void Notify(string title, string text)
    {
        if (GetSystemService(NotificationService) is not NotificationManager manager) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(Manifest.Permission.PostNotifications) != global::Android.Content.PM.Permission.Granted)
        {
            RequestPermissions([Manifest.Permission.PostNotifications], 1);
            return;
        }
        var open = PendingIntent.GetActivity(this, 0, new Intent(this, typeof(MainActivity)).SetFlags(ActivityFlags.SingleTop | ActivityFlags.ClearTop),
            PendingIntentFlags.Immutable | PendingIntentFlags.UpdateCurrent);
        using var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle(title)
            .SetContentText(text)
            .SetStyle(new Notification.BigTextStyle().BigText(text))
            .SetSmallIcon(global::Android.Resource.Drawable.IcDialogInfo)
            .SetContentIntent(open)
            .SetAutoCancel(true)
            .Build();
        manager.Notify(Interlocked.Increment(ref notificationId), notification);
    }

    public void SetSecure(bool secure) => RunOnUiThread(() =>
    {
        if (Window == null) return;
        if (secure) Window.SetFlags(WindowManagerFlags.Secure, WindowManagerFlags.Secure);
        else Window.ClearFlags(WindowManagerFlags.Secure);
    });

    private void EnsureChannel()
    {
        if (GetSystemService(NotificationService) is not NotificationManager manager) return;
        using var channel = new NotificationChannel(ChannelId, "Поступления", NotificationImportance.Default)
        {
            Description = "Входящие переводы и награды за стейкинг",
        };
        manager.CreateNotificationChannel(channel);
    }
}
