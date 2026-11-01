using Stopwatch = System.Diagnostics.Stopwatch;
using Android;
using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.Graphics;
using Android.OS;
using Android.Util;
using Android.Views;
using Android.Widget;
using AndroidX.Activity;
using AndroidX.Camera.Core;
using AndroidX.Camera.View;
using AndroidX.Core.Content;
using AndroidX.Core.View;
using Java.Util.Concurrent;
using MinterWallet.Mobile;
using AView = Android.Views.View;
using LayoutParams = Android.Views.ViewGroup.LayoutParams;

namespace MinterWallet.Android;

// A private activity, opened only by the wallet's scan action. CameraX owns camera rotation, focus and lifecycle.
// Frames are decoded locally, never saved. A generation keeps late callbacks out of a subsequent scan.
[Activity(Theme = "@style/MyTheme.NoActionBar", Exported = false, ScreenOrientation = ScreenOrientation.Portrait)]
public sealed class QrScanActivity : ComponentActivity
{
    private static readonly object sync = new();
    private sealed record Request(long Id, TaskCompletionSource<string?> Completion);
    private static Request? pending;
    private static WeakReference<QrScanActivity>? opened;
    private static long generation;
    private long id;
    private bool permissionPending;
    private LifecycleCameraController? controller;
    private IExecutorService? executor;
    private PreviewView? preview;

    public static Task<string?> ScanAsync(Activity owner)
    {
        Request request;
        lock (sync)
        {
            if (pending != null) return pending.Completion.Task;
            request = pending = new(++generation, new(TaskCreationOptions.RunContinuationsAsynchronously));
        }
        owner.RunOnUiThread(() =>
        {
            try { if (Active(request.Id)) owner.StartActivity(new Intent(owner, typeof(QrScanActivity)).PutExtra("request", request.Id)); }
            catch (Exception) { Complete(request.Id, error: "Не удалось открыть камеру. Вставьте адрес вручную."); }
        });
        return request.Completion.Task;
    }

    public static void Cancel()
    {
        long current;
        lock (sync) current = pending?.Id ?? 0;
        Complete(current);
    }

    private static bool Active(long request) { lock (sync) return pending?.Id == request; }
    private static void Complete(long request, string? text = null, string? error = null)
    {
        Request? completed;
        QrScanActivity? activity = null;
        lock (sync)
        {
            if (pending?.Id != request) return;
            completed = pending; pending = null;
            opened?.TryGetTarget(out activity); opened = null;
        }
        activity?.RunOnUiThread(activity.Finish);
        if (error == null) completed!.Completion.TrySetResult(text);
        else completed!.Completion.TrySetException(new InvalidOperationException(Localization.Text(error)));
    }

    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        id = Intent?.GetLongExtra("request", 0) ?? 0;
        if (!Active(id)) { Finish(); return; }
        lock (sync) opened = new(this);
        Window?.AddFlags(WindowManagerFlags.Secure);
        var root = new LinearLayout(this) { Orientation = Orientation.Vertical };
        root.SetBackgroundColor(Color.Rgb(14, 18, 24));
        ViewCompat.SetOnApplyWindowInsetsListener(root, new Insets());
        var header = new LinearLayout(this) { Orientation = Orientation.Horizontal };
        header.SetGravity(GravityFlags.CenterVertical);
        header.SetPadding(Dp(16), Dp(8), Dp(8), Dp(8));
        var title = new TextView(this) { Text = Localization.Text("Сканировать QR"), Typeface = Typeface.DefaultBold };
        title.SetTextSize(ComplexUnitType.Sp, 20); title.SetTextColor(Color.White);
        header.AddView(title, new LinearLayout.LayoutParams(0, Dp(48), 1)); title.Gravity = GravityFlags.CenterVertical;
        var close = new Button(this) { Text = Localization.Text("Отмена"), ContentDescription = Localization.Text("Закрыть сканер") };
        close.SetTextColor(Color.Rgb(105, 162, 255)); close.SetBackgroundColor(Color.Transparent);
        close.Click += (_, _) => Complete(id);
        header.AddView(close, new LinearLayout.LayoutParams(LayoutParams.WrapContent, Dp(48)));
        root.AddView(header);
        var camera = new FrameLayout(this);
        preview = new PreviewView(this);
        camera.AddView(preview, new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
        camera.AddView(new Guide(this), new FrameLayout.LayoutParams(LayoutParams.MatchParent, LayoutParams.MatchParent));
        root.AddView(camera, new LinearLayout.LayoutParams(LayoutParams.MatchParent, 0, 1));
        var hint = new TextView(this) { Text = Localization.Text("Наведите камеру на QR с адресом Minter или запросом перевода."), Gravity = GravityFlags.Center };
        hint.SetTextSize(ComplexUnitType.Sp, 14); hint.SetTextColor(Color.Rgb(158, 171, 189)); hint.SetPadding(Dp(24), Dp(18), Dp(24), Dp(18));
        root.AddView(hint);
        SetContentView(root); ViewCompat.RequestApplyInsets(root);
        if (CheckSelfPermission(Manifest.Permission.Camera) == Permission.Granted) StartCamera();
        else { permissionPending = true; RequestPermissions([Manifest.Permission.Camera], 83); }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode != 83) return;
        permissionPending = false;
        if (grantResults.Length > 0 && grantResults[0] == Permission.Granted && Active(id)) StartCamera();
        else Complete(id, error: "Нет доступа к камере. Разрешите его в настройках приложения или вставьте адрес.");
    }

    private void StartCamera()
    {
        if (controller != null || !Active(id)) return;
        try
        {
            executor = Executors.NewSingleThreadExecutor();
            controller = new LifecycleCameraController(this) { ImageAnalysisBackpressureStrategy = ImageAnalysis.StrategyKeepOnlyLatest };
            controller.SetEnabledUseCases(CameraController.ImageAnalysis);
            controller.SetImageAnalysisAnalyzer(executor!, new Analyzer(this));
            preview!.Controller = controller;
            controller.BindToLifecycle(this);
            var ready = controller.InitializationFuture!;
            ready.AddListener(new Java.Lang.Runnable(() =>
            {
                if (!Active(id)) return;
                try { _ = ready.Get(); }
                catch (Exception) { Complete(id, error: "Камера недоступна. Закройте другие приложения с камерой или вставьте адрес."); }
            }), ContextCompat.GetMainExecutor(this));
        }
        catch (Exception) { Complete(id, error: "Камера недоступна. Закройте другие приложения с камерой или вставьте адрес."); }
    }

    protected override void OnStop()
    {
        controller?.ClearImageAnalysisAnalyzer();
        controller?.Unbind();
        executor?.ShutdownNow();
        base.OnStop();
        if (!permissionPending) Complete(id);
    }

    protected override void OnDestroy()
    {
        Complete(id);
        controller?.Dispose(); executor?.Dispose();
        base.OnDestroy();
    }

    private int Dp(int value) => (int)(value * (Resources?.DisplayMetrics?.Density ?? 1));
    private sealed class Insets : Java.Lang.Object, IOnApplyWindowInsetsListener
    {
        public WindowInsetsCompat? OnApplyWindowInsets(AView? view, WindowInsetsCompat? insets)
        {
            if (insets == null) return null;
            var safe = insets.GetInsets(WindowInsetsCompat.Type.SystemBars() | WindowInsetsCompat.Type.DisplayCutout());
            if (safe != null) view?.SetPadding(safe.Left, safe.Top, safe.Right, safe.Bottom);
            return insets;
        }
    }

    private sealed class Analyzer(QrScanActivity activity) : Java.Lang.Object, ImageAnalysis.IAnalyzer
    {
        private long last;
        public void Analyze(IImageProxy? image)
        {
            if (image == null) return;
            try
            {
                if (!Active(activity.id) || Stopwatch.GetElapsedTime(last) < TimeSpan.FromMilliseconds(180)) return;
                last = Stopwatch.GetTimestamp();
                var planes = image.GetPlanes();
                if (planes == null || planes.Length == 0) return;
                var plane = planes[0];
                var buffer = plane.Buffer;
                if (buffer == null) return;
                var bytes = new byte[buffer.Remaining()]; buffer.Get(bytes);
                var crop = image.CropRect;
                if (crop == null) return;
                var width = crop.Width(); var height = crop.Height();
                var luminance = new byte[width * height];
                for (var y = 0; y < height; y++)
                {
                    var offset = (crop.Top + y) * plane.RowStride + crop.Left * plane.PixelStride;
                    if (plane.PixelStride == 1) Buffer.BlockCopy(bytes, offset, luminance, y * width, width);
                    else for (var x = 0; x < width; x++) luminance[y * width + x] = bytes[offset + x * plane.PixelStride];
                }
                var text = QrDecoder.Decode(luminance, width, height);
                if (text != null) activity.RunOnUiThread(() => Complete(activity.id, text));
            }
            catch (Exception e) when (e is ObjectDisposedException or Java.Lang.IllegalStateException or ArgumentException) { }
            finally { image.Close(); }
        }
    }

    private sealed class Guide(Context context) : AView(context)
    {
        protected override void OnDraw(Canvas canvas)
        {
            base.OnDraw(canvas);
            var size = Math.Min(Width, Height) * .72f;
            using var paint = new Paint { Color = Color.Rgb(105, 162, 255), StrokeWidth = 3, AntiAlias = true };
            paint.SetStyle(Paint.Style.Stroke);
            canvas.DrawRoundRect((Width - size) / 2, (Height - size) / 2, (Width + size) / 2, (Height + size) / 2, 24, 24, paint);
        }
    }
}
