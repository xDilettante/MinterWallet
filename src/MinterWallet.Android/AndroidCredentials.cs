using System.Security.Cryptography;
using System.Text;
using Android.App;
using Android.Content;
using Android.Hardware.Biometrics;
using Android.Hardware.Fingerprints;
using Android.OS;
using Android.Security.Keystore;
using Java.Security;
using Javax.Crypto;
using Javax.Crypto.Spec;
using CipherMode = Javax.Crypto.CipherMode;

namespace MinterWallet.Android;

// Keys never leave AndroidKeyStore. PIN derivation binds copied wallet files to this app installation;
// biometric decryption additionally requires a fresh, cryptographically bound strong biometric authentication.
internal sealed class AndroidCredentials(MainActivity activity, Action<string>? diagnostic = null)
{
    private const string BioAlias = "minterwallet_bio_v2";
    private const string PinAlias = "minterwallet_pin_v1";
    private static readonly byte[] Aad = "minterwallet-biometric-v2"u8.ToArray();
    private CancellationSignal? cancellation;
    private TaskCompletionSource<Cipher?>? pending;
    private string DirectoryPath => activity.FilesDir!.AbsolutePath;
    private string FilePath => Path.Combine(DirectoryPath, "biometric-v2.dat");
    public string Error { get; private set; } = "Биометрия недоступна. Введите PIN-код или пароль.";
    public bool HasSecret => File.Exists(FilePath);

    public void DiscardLegacySecret()
    {
        foreach (var suffix in new[] { "", ".bak", ".tmp" })
        {
            var legacy = Path.Combine(DirectoryPath, "biometric.dat" + suffix);
            if (File.Exists(legacy)) File.Delete(legacy);
        }
        using var store = OpenStore();
        if (store.ContainsAlias("minterwallet_bio_key")) store.DeleteEntry("minterwallet_bio_key");
    }

    public bool Available
    {
        get
        {
            if (!OperatingSystem.IsAndroidVersionAtLeast(28)) return false;
            if (OperatingSystem.IsAndroidVersionAtLeast(30))
                return activity.GetSystemService(Context.BiometricService) is BiometricManager manager && manager.CanAuthenticate(15 /* BIOMETRIC_STRONG */) == BiometricCode.Success;
            // CryptoObject on API 28/29 supports strong fingerprints; never promise availability just for a screen lock.
#pragma warning disable CS0618, CA1422
            return activity.GetSystemService(Context.FingerprintService) is FingerprintManager { IsHardwareDetected: true, HasEnrolledFingerprints: true };
#pragma warning restore CS0618, CA1422
        }
    }

    private static KeyStore OpenStore()
    {
        var store = KeyStore.GetInstance("AndroidKeyStore")!;
        store.Load(null);
        return store;
    }

    public string DerivePin(string pin)
    {
        using var store = OpenStore();
        var key = store.GetKey(PinAlias, null);
        if (key == null)
        {
            using var generator = KeyGenerator.GetInstance("HmacSHA256", "AndroidKeyStore")!;
            using var spec = new KeyGenParameterSpec.Builder(PinAlias, KeyStorePurpose.Sign)
                .SetDigests(KeyProperties.DigestSha256).SetKeySize(256).Build();
            generator.Init(spec);
            key = generator.GenerateKey();
        }
        using var mac = Mac.GetInstance("HmacSHA256")!;
        mac.Init(key);
        var input = Encoding.UTF8.GetBytes("minterwallet-pin-v1\0" + pin);
        byte[]? result = null;
        try { result = mac.DoFinal(input)!; return Convert.ToHexStringLower(result); }
        finally { CryptographicOperations.ZeroMemory(input); if (result != null) CryptographicOperations.ZeroMemory(result); }
    }

    public async Task<bool> EnableAsync(string secret)
    {
        if (!Available || pending != null) return false;
        var stage = "create-key";
        try
        {
            DiscardLegacySecret();
            using var store = OpenStore();
            // A fresh alias cannot accidentally reuse the old, non-authenticated key from the experimental build.
            if (store.ContainsAlias(BioAlias)) store.DeleteEntry(BioAlias);
            using var generator = KeyGenerator.GetInstance(KeyProperties.KeyAlgorithmAes, "AndroidKeyStore")!;
            using var spec = new KeyGenParameterSpec.Builder(BioAlias, KeyStorePurpose.Encrypt | KeyStorePurpose.Decrypt)
                .SetBlockModes(KeyProperties.BlockModeGcm).SetEncryptionPaddings(KeyProperties.EncryptionPaddingNone)
                .SetKeySize(256).SetUserAuthenticationRequired(true).SetInvalidatedByBiometricEnrollment(true).Build();
            generator.Init(spec);
            using var key = generator.GenerateKey()!;
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            cipher.Init(CipherMode.EncryptMode, key);
            stage = "authenticate";
            using var authorized = await PromptAsync(cipher, "Minter Wallet", Localization.Text("Подтвердите включение биометрии"), Localization.Text("Не сейчас"));
            if (authorized == null) return false;
            stage = "encrypt";
            // updateAAD reaches KeyStore too: before authentication Android caches KEY_USER_NOT_AUTHENTICATED,
            // so even a successful fingerprint cannot rescue that Cipher's later doFinal.
            authorized.UpdateAAD(Aad);
            var plain = Encoding.UTF8.GetBytes(secret);
            try
            {
                var encrypted = authorized.DoFinal(plain)!;
                var iv = authorized.GetIV()!;
                var payload = new byte[1 + iv.Length + encrypted.Length];
                payload[0] = (byte)iv.Length;
                iv.CopyTo(payload, 1); encrypted.CopyTo(payload, 1 + iv.Length);
                Core.WalletPaths.WriteAtomic(FilePath, payload, secret: true);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            return true;
        }
        catch (Exception error) { LogFailure(stage, error); Error = "Не удалось включить биометрию. Используйте PIN-код или пароль."; return false; }
    }

    public async Task<string?> ReadAsync(string title, string subtitle)
    {
        if (!Available || !HasSecret || pending != null) return null;
        var stage = "read-key";
        try
        {
            if (new FileInfo(FilePath).Length > 65536) throw new CryptographicException();
            var payload = File.ReadAllBytes(FilePath);
            if (payload.Length < 29 || payload[0] != 12) throw new CryptographicException();
            using var store = OpenStore();
            using var key = store.GetKey(BioAlias, null) ?? throw new CryptographicException();
            using var cipher = Cipher.GetInstance("AES/GCM/NoPadding")!;
            using var spec = new GCMParameterSpec(128, payload.AsSpan(1, 12).ToArray());
            cipher.Init(CipherMode.DecryptMode, key, spec);
            using var authorized = await PromptAsync(cipher, title, subtitle, Localization.Text("PIN-код / пароль"));
            if (authorized == null) return null;
            stage = "decrypt";
            authorized.UpdateAAD(Aad);
            var plain = authorized.DoFinal(payload.AsSpan(13).ToArray())!;
            try { return Encoding.UTF8.GetString(plain); }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception error)
        {
            LogFailure(stage, error);
            Clear();
            Error = "Биометрия сброшена или недоступна. Введите PIN-код или пароль и включите её заново в настройках.";
            return null;
        }
    }

    // Only operation/type codes: never log the credential, file contents or exception messages.
    private void LogFailure(string stage, Exception error)
    {
        var cause = (error as Java.Lang.Throwable)?.Cause;
        Trace($"Biometric {stage}: {error.GetType().Name}; cause={cause?.GetType().Name ?? "none"}");
    }

    private void Trace(string code) { global::Android.Util.Log.Info("MinterWallet", code); diagnostic?.Invoke(code); }

    private async Task<Cipher?> PromptAsync(Cipher cipher, string title, string subtitle, string fallback)
    {
        if (!OperatingSystem.IsAndroidVersionAtLeast(28) || pending != null) return null;
        var completion = new TaskCompletionSource<Cipher?>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending = completion;
        cancellation = new CancellationSignal();
        try
        {
            activity.RunOnUiThread(() =>
            {
                if (!OperatingSystem.IsAndroidVersionAtLeast(28)) { completion.TrySetResult(null); return; }
                if (completion.Task.IsCompleted) return;
                try
                {
                    using var builder = new BiometricPrompt.Builder(activity).SetTitle(title).SetSubtitle(subtitle)
                        .SetNegativeButton(fallback, activity.MainExecutor!, new NegativeListener(() => completion.TrySetResult(null)));
                    var prompt = builder.Build();
                    prompt.Authenticate(new BiometricPrompt.CryptoObject(cipher), cancellation, activity.MainExecutor!, new Callback(completion, this));
                }
                catch (Exception error) { LogFailure("prompt", error); completion.TrySetResult(null); }
            });
            return await completion.Task;
        }
        finally
        {
            cancellation?.Cancel(); cancellation?.Dispose(); cancellation = null;
            if (ReferenceEquals(pending, completion)) pending = null;
        }
    }

    public void Cancel() { cancellation?.Cancel(); pending?.TrySetResult(null); }

    public void Clear()
    {
        Cancel();
        foreach (var file in new[] { FilePath, FilePath + ".bak", FilePath + ".tmp" })
            if (File.Exists(file)) File.Delete(file);
        using var store = OpenStore();
        if (store.ContainsAlias(BioAlias)) store.DeleteEntry(BioAlias);
        DiscardLegacySecret();
    }

    private sealed class NegativeListener(Action cancel) : Java.Lang.Object, IDialogInterfaceOnClickListener
    {
        public void OnClick(IDialogInterface? dialog, int which) => cancel();
    }

    [System.Runtime.Versioning.SupportedOSPlatform("android28.0")]
    private sealed class Callback(TaskCompletionSource<Cipher?> completion, AndroidCredentials owner) : BiometricPrompt.AuthenticationCallback
    {
        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult? result)
        {
            owner.Trace("Biometric prompt succeeded");
            completion.TrySetResult(result?.CryptoObject?.Cipher);
        }
        public override void OnAuthenticationError(BiometricErrorCode code, Java.Lang.ICharSequence? message)
        {
            owner.Trace($"Biometric prompt ended: {(int)code}");
            owner.Error = "Биометрия не выполнена. Введите PIN-код или пароль.";
            completion.TrySetResult(null);
        }
    }
}
