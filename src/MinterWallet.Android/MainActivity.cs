using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Android.Runtime;
using Android.Views;
using Avalonia;
using Avalonia.Android;

namespace MinterWallet.Android;

[Application]
public sealed class WalletAndroidApplication : AvaloniaAndroidApplication<App>
{
    public WalletAndroidApplication(nint javaReference, JniHandleOwnership transfer) : base(javaReference, transfer) { }

    public override void OnCreate()
    {
        MobileHost.DataDirectory = FilesDir?.AbsolutePath;
        if (string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("LANG")))
            System.Environment.SetEnvironmentVariable("LANG", Java.Util.Locale.Default?.Language ?? "en");
        base.OnCreate();
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
    LaunchMode = LaunchMode.SingleTop,
    WindowSoftInputMode = SoftInput.AdjustResize,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode | ConfigChanges.ScreenLayout | ConfigChanges.SmallestScreenSize | ConfigChanges.Density)]
public sealed class MainActivity : AvaloniaMainActivity, IMobilePlatform
{
    private const string ChannelId = "minter_arrivals";
    private int notificationId;

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        MobileHost.DataDirectory ??= FilesDir?.AbsolutePath;
        MobileHost.Platform = this;
        base.OnCreate(savedInstanceState);
        EnsureChannel();
    }

    public void Notify(string title, string text)
    {
        if (GetSystemService(NotificationService) is not NotificationManager manager) return;
        if (OperatingSystem.IsAndroidVersionAtLeast(33) && CheckSelfPermission(Manifest.Permission.PostNotifications) != Permission.Granted)
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
