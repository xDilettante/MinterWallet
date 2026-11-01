using Android.App;
using Android.OS;
using Android.Widget;

namespace MinterWallet.Android
{
    // Runs the actual wallet service in a different Android sandbox, with a disposable public test string.
    // No wallet, mnemonic, password or live transaction is used or accessed by this app.
    [Activity(Label = "Minter biometric check", MainLauncher = true, Exported = true)]
    public sealed class MainActivity : Activity
    {
        private AndroidCredentials credentials = null!;
        private TextView status = null!;
        private const string TestSecret = "public-biometric-regression-check";

        protected override void OnCreate(Bundle? savedInstanceState)
        {
            base.OnCreate(savedInstanceState);
            credentials = new AndroidCredentials(this, code =>
            {
                File.AppendAllText(Path.Combine(FilesDir!.AbsolutePath, "diagnostics.txt"), code + "\n");
                RunOnUiThread(() => status.Text = code);
            });
            var content = new LinearLayout(this) { Orientation = Orientation.Vertical };
            content.SetPadding(24, 48, 24, 24);
            status = new TextView(this) { Text = "Проверка биометрии. Тестовые данные; кошелёк не используется.", TextSize = 20 };
            content.AddView(status);
            AddButton(content, "1. Включить (отпечаток)", async () =>
            {
                var success = await credentials.EnableAsync(TestSecret);
                Report(success && credentials.HasSecret ? "PASS enable" : "FAIL enable: " + credentials.Error);
            });
            AddButton(content, "2. Прочитать (отпечаток)", async () =>
            {
                var actual = await credentials.ReadAsync("Minter biometric check", "Проверка расшифровки тестовой строки");
                Report(actual == TestSecret ? "PASS decrypt" : "FAIL decrypt: " + credentials.Error);
            });
            AddButton(content, "3. Сбросить только тест", () => { credentials.Clear(); Report("Test reset"); return Task.CompletedTask; });
            SetContentView(content);
        }

        private void AddButton(LinearLayout panel, string title, Func<Task> run)
        {
            var button = new Button(this) { Text = title };
            button.Click += async (_, _) =>
            {
                button.Enabled = false;
                try { await run(); }
                catch (Exception error) { Report("FAIL: " + error.GetType().Name); }
                finally { button.Enabled = true; }
            };
            panel.AddView(button);
        }

        private void Report(string text)
        {
            RunOnUiThread(() => status.Text = text);
            File.AppendAllText(Path.Combine(FilesDir!.AbsolutePath, "diagnostics.txt"), text + "\n");
            global::Android.Util.Log.Info("MinterWalletBioCheck", text);
        }

        protected override void OnStop() { credentials.Cancel(); base.OnStop(); }
    }
}

namespace MinterWallet
{
    internal static class Localization { public static string Text(string value) => value; }
}
