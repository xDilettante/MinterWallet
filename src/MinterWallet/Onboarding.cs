using System.Security.Cryptography;
using Minter.Protocol;
using MinterWallet.Core;

namespace MinterWallet;

public sealed class WelcomeViewModel(Shell shell) : Observable
{
    public Command Create { get; } = new(() => shell.Show(new CreateWalletViewModel(shell)));
    public Command Restore { get; } = new(() => shell.Show(new ImportViewModel(shell, ImportKind.Mnemonic)));
    public Command ImportKey { get; } = new(() => shell.Show(new ImportViewModel(shell, ImportKind.PrivateKey)));
    public Command Watch { get; } = new(() => shell.Show(new ImportViewModel(shell, ImportKind.WatchOnly)));
    public string DataDirectory => shell.DataDirectory;
    public string Version => $"Версия {Program.Version}";
}

public sealed record PhraseWord(int Number, string Word);

// New wallet: 12 or 24 words (copy or write them down, as the user prefers), an optional check of three of them that can
// be skipped, then the password. Nothing here blocks the user: the phrase stays available later in the settings.
public sealed class CreateWalletViewModel : ScreenModel
{
    private readonly Shell shell;
    private string mnemonic = Mnemonic.Generate(12);
    private int wordCountIndex;
    private int step = 1;
    private string answer1 = "", answer2 = "", answer3 = "", password = "", repeat = "";
    private bool busy, verified;

    public CreateWalletViewModel(Shell shell)
    {
        this.shell = shell;
        Words = []; Ask = [];
        UsePhrase();
        Next = new Command(() => { Say(""); Step = 2; });
        Back = new Command(() => { Say(""); if (Step > 1) Step--; else shell.ShowWelcome(); });
        Copy = new AsyncCommand(CopyAsync, Fail);
        Check = new Command(CheckWords);
        Skip = new Command(() => { Say(""); Step = 3; });
        Finish = new AsyncCommand(FinishAsync, Fail, () => !Busy);
    }

    public IReadOnlyList<PhraseWord> Words { get; private set; }
    public int[] Ask { get; private set; }

    // 12 words (128 bits, as most wallets make) or 24 (256 bits); a new phrase on each change, before anything is saved.
    public int WordCountIndex
    {
        get => wordCountIndex;
        set
        {
            if (value < 0 || !Set(ref wordCountIndex, value)) return;
            mnemonic = Mnemonic.Generate(value == 1 ? 24 : 12);
            UsePhrase();
        }
    }

    private void UsePhrase()
    {
        Words = mnemonic.Split(' ').Select((w, i) => new PhraseWord(i + 1, w)).ToArray();
        Ask = Enumerable.Range(0, Words.Count).OrderBy(_ => RandomNumberGenerator.GetInt32(1000)).Take(3).Order().ToArray();
        answer1 = answer2 = answer3 = ""; verified = false;
        foreach (var n in new[] { nameof(Words), nameof(Ask), nameof(Ask1), nameof(Ask2), nameof(Ask3), nameof(CheckHint), nameof(Answer1), nameof(Answer2), nameof(Answer3) }) Changed(n);
    }
    public string Ask1 => $"Слово № {Ask[0] + 1}";
    public string Ask2 => $"Слово № {Ask[1] + 1}";
    public string Ask3 => $"Слово № {Ask[2] + 1}";
    public string CheckHint => $"Введите слова № {Ask[0] + 1}, {Ask[1] + 1} и {Ask[2] + 1} — или вставьте всю фразу в любое поле. " +
        "Шаг можно пропустить: фраза останется доступна в разделе «Счета» по паролю.";
    public int Step
    {
        get => step;
        set
        {
            if (!Set(ref step, value)) return;
            foreach (var name in new[] { nameof(IsStep1), nameof(IsStep2), nameof(IsStep3), nameof(Past1), nameof(Past2) }) Changed(name);
        }
    }
    public bool IsStep1 => Step == 1;
    public bool IsStep2 => Step == 2;
    public bool IsStep3 => Step == 3;
    public bool Past1 => Step > 1;
    public bool Past2 => Step > 2;
    public bool Verified { get => verified; private set => Set(ref verified, value); }
    public string Answer1 { get => answer1; set => Answer(ref answer1, value); }
    public string Answer2 { get => answer2; set => Answer(ref answer2, value); }
    public string Answer3 { get => answer3; set => Answer(ref answer3, value); }
    public string Password { get => password; set { Set(ref password, value); Edited(); } }
    public string Repeat { get => repeat; set { Set(ref repeat, value); Edited(); } }
    public string PasswordHint => Passwords.Hint(Password, Repeat);
    public bool Busy { get => busy; set { Set(ref busy, value); Finish.Refresh(); } }
    public Command Next { get; }
    public Command Back { get; }
    public AsyncCommand Copy { get; }
    public Command Check { get; }
    public Command Skip { get; }
    public AsyncCommand Finish { get; }

    private void Edited()
    {
        Changed(nameof(PasswordHint));
        if (Tone == Tone.Error) Say("");
    }

    private async Task CopyAsync()
    {
        if (shell.CopyText == null) return;
        await shell.CopyText(mnemonic);
        Say("Фраза скопирована. Сохраните её там, где её никто не увидит: в менеджере паролей или на бумаге.", Tone.Success);
    }

    // A whole phrase pasted into any of the three fields fills all three from it.
    private void Answer(ref string field, string value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var words = Display.PhraseWords(value);
        if (words.Length == Words.Count)
        {
            answer1 = words[Ask[0]]; answer2 = words[Ask[1]]; answer3 = words[Ask[2]];
            Changed(nameof(Answer1)); Changed(nameof(Answer2)); Changed(nameof(Answer3));
            return;
        }
        Set(ref field, value, name);
    }

    private void CheckWords()
    {
        var words = mnemonic.Split(' ');
        var answers = new[] { Answer1, Answer2, Answer3 };
        if (Ask.Where((p, i) => !string.Equals(words[p], answers[i].Trim(), StringComparison.OrdinalIgnoreCase)).Any())
        {
            Say("Не все слова совпали. Проверьте сохранённую фразу — или пропустите этот шаг.", Tone.Warning);
            return;
        }
        Verified = true;
        Say("Фраза сохранена верно.", Tone.Success);
        Step = 3;
    }

    private async Task FinishAsync()
    {
        if (Passwords.Problem(Password, Repeat) is { } problem) { Say(problem, Tone.Error); return; }
        Busy = true;
        Say("Шифрование ключей…", Tone.Progress);
        try
        {
            var (store, session) = await Task.Run(() => Keystore.Create(shell.KeystorePath, Password));
            session.AddMnemonic(mnemonic, "Основной");
            shell.Opened(store, session);
        }
        finally { Busy = false; }
    }
}

// Password rules shown while typing and checked before the key store is written.
public static class Passwords
{
    public const int Minimum = 8;

    public static string Hint(string password, string repeat) =>
        password.Length == 0 ? $"Не короче {Minimum} символов."
        : password.Length < Minimum ? $"Ещё {Minimum - password.Length} {Chars(Minimum - password.Length)} — не короче {Minimum}."
        : repeat.Length == 0 ? "Повторите пароль."
        : repeat != password ? "Пароли пока не совпадают."
        : "Пароли совпадают.";

    public static string? Problem(string password, string repeat) =>
        password.Length < Minimum ? $"Пароль — не короче {Minimum} символов."
        : password != repeat ? "Пароли не совпадают."
        : null;

    private static string Chars(int n) => n % 10 == 1 && n % 100 != 11 ? "символ" : n % 10 is >= 2 and <= 4 && n % 100 is < 12 or > 14 ? "символа" : "символов";
}

public enum ImportKind { Mnemonic, PrivateKey, WatchOnly }

// A phrase, key or address as the user typed or pasted it: checked while typing, then added to the key store.
public static class Secrets
{
    // Null when valid, "" when empty, otherwise what is wrong.
    public static string? Problem(ImportKind kind, string text)
    {
        text = text.Trim();
        if (text.Length == 0) return "";
        return kind switch
        {
            ImportKind.Mnemonic => Display.PhraseProblem(text),
            ImportKind.PrivateKey => KeyHex(text) is { Length: 64 } hex && Hex.IsHex(hex) ? null : "Ключ — 64 шестнадцатеричных символа (можно с 0x).",
            _ => Address.TryParse(text, out _) ? null : "Адрес — Mx и 40 шестнадцатеричных символов."
        };
    }

    public static string Check(ImportKind kind, string text) => Problem(kind, text) ?? kind switch
    {
        ImportKind.Mnemonic => $"{Display.Words(Display.PhraseWords(text).Length)}, фраза верна.",
        ImportKind.PrivateKey => "Ключ верного формата.",
        _ => "Адрес верного формата."
    };

    public static Tone ToneOf(ImportKind kind, string text) => text.Trim().Length == 0 ? Tone.None : Problem(kind, text) == null ? Tone.Success : Tone.Warning;

    public static string Clean(ImportKind kind, string text) => kind == ImportKind.Mnemonic ? Display.Phrase(text) : text.Trim();

    public static WalletAccount Add(KeystoreSession session, ImportKind kind, string text, string name)
    {
        switch (kind)
        {
            case ImportKind.Mnemonic: return session.AddMnemonic(Display.Phrase(text), name);
            case ImportKind.PrivateKey:
                var bytes = Hex.Parse(KeyHex(text.Trim()));
                try { return session.AddPrivateKey(bytes, name); } finally { CryptographicOperations.ZeroMemory(bytes); }
            default: return session.AddWatchOnly(Address.Parse(text.Trim()), name);
        }
    }

    private static string KeyHex(string text) => text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? text[2..] : text;
}

public sealed class ImportViewModel : ScreenModel
{
    private readonly Shell shell;
    private string secret = "", name = "Основной", password = "", repeat = "";
    private bool busy;

    public ImportViewModel(Shell shell, ImportKind kind)
    {
        this.shell = shell; Kind = kind;
        Back = new Command(shell.ShowWelcome);
        Paste = new AsyncCommand(PasteAsync, Fail);
        Finish = new AsyncCommand(FinishAsync, Fail, () => !Busy);
    }

    public ImportKind Kind { get; }
    public string Title => Kind switch { ImportKind.Mnemonic => "Восстановить по фразе", ImportKind.PrivateKey => "Импорт приватного ключа", _ => "Только просмотр" };
    public string Hint => Kind switch
    {
        ImportKind.Mnemonic => "12 или 24 слова из этого или другого кошелька Minter (путь m/44'/60'/0'/0/0). Можно вставить фразу как есть — с номерами, запятыми и переносами строк.",
        ImportKind.PrivateKey => "Ключ сохранится только на этом устройстве, зашифрованный паролем кошелька.",
        _ => "Кошелёк покажет балансы и историю адреса, но подписывать с него не сможет."
    };
    public string SecretLabel => Kind switch { ImportKind.Mnemonic => "Фраза восстановления", ImportKind.PrivateKey => "Приватный ключ", _ => "Адрес" };
    public string SecretPlaceholder => Kind switch { ImportKind.Mnemonic => "word word word …", ImportKind.PrivateKey => "64 шестнадцатеричных символа", _ => "Mx…" };
    public string Secret { get => secret; set { Set(ref secret, value); Changed(nameof(SecretCheck)); Changed(nameof(SecretTone)); Edited(); } }
    public string Name { get => name; set => Set(ref name, value); }
    public string Password { get => password; set { Set(ref password, value); Changed(nameof(PasswordHint)); Edited(); } }
    public string Repeat { get => repeat; set { Set(ref repeat, value); Changed(nameof(PasswordHint)); Edited(); } }
    public string PasswordHint => Passwords.Hint(Password, Repeat);
    public bool Busy { get => busy; set { Set(ref busy, value); Finish.Refresh(); } }
    public bool IsPhrase => Kind == ImportKind.Mnemonic;
    public bool HidesSecret => Kind == ImportKind.PrivateKey;
    public double SecretHeight => IsPhrase ? 84 : 0;
    public AsyncCommand Paste { get; }
    public Command Back { get; }
    public AsyncCommand Finish { get; }

    // Checked while typing, so a mistake shows before the password is entered.
    public string SecretCheck => Secrets.Check(Kind, Secret);
    public Tone SecretTone => Secrets.ToneOf(Kind, Secret);

    private void Edited() { if (Tone == Tone.Error) Say(""); }

    private async Task PasteAsync()
    {
        var text = shell.PasteText == null ? null : await shell.PasteText();
        if (string.IsNullOrWhiteSpace(text)) { Say("В буфере обмена нет текста.", Tone.Warning); return; }
        Secret = Secrets.Clean(Kind, text);
        Say("");
    }

    private async Task FinishAsync()
    {
        Say("");
        var text = Secret.Trim();
        // Validate before creating anything on disk.
        if (Secrets.Problem(Kind, text) is { } problem) { Say(problem.Length == 0 ? $"Укажите: {SecretLabel.ToLowerInvariant()}." : problem, Tone.Error); return; }
        if (Passwords.Problem(Password, Repeat) is { } bad) { Say(bad, Tone.Error); return; }
        Busy = true;
        Say("Шифрование ключей…", Tone.Progress);
        try
        {
            var (store, session) = await Task.Run(() => Keystore.Create(shell.KeystorePath, Password));
            try { Secrets.Add(session, Kind, text, Name); }
            catch { session.Dispose(); File.Delete(shell.KeystorePath); File.Delete(shell.KeystorePath + ".bak"); throw; }
            Secret = "";
            shell.Opened(store, session);
        }
        finally { Busy = false; }
    }
}

public sealed class UnlockViewModel : ScreenModel
{
    private readonly Shell shell;
    private string password = "";
    private bool busy, forgot;

    public UnlockViewModel(Shell shell)
    {
        this.shell = shell;
        Unlock = new AsyncCommand(UnlockAsync, Fail, () => !Busy);
        Forgot = new Command(() => Forgetting = !Forgetting);
        StartOver = new Command(() => shell.StartOver());
    }

    public string Password { get => password; set { Set(ref password, value); if (Tone == Tone.Error) Say(""); } }
    public bool Busy { get => busy; set { Set(ref busy, value); Unlock.Refresh(); } }
    public bool Forgetting { get => forgot; set => Set(ref forgot, value); }
    public string Accounts => string.Join(", ", shell.Store!.Accounts.Select(a => a.Name));
    public string StartOverText => $"Пароль нигде не хранится и не восстанавливается. Если у вас сохранена фраза или ключ, восстановите кошелёк заново: " +
        $"нынешний файл ключей не удалится, а будет переименован в {Keystore.FileName}.old-…";
    public AsyncCommand Unlock { get; }
    public Command Forgot { get; }
    public Command StartOver { get; }

    private async Task UnlockAsync()
    {
        if (Password.Length == 0) { Say("Введите пароль.", Tone.Warning); return; }
        Say(""); Busy = true;
        try
        {
            var session = await Task.Run(() => shell.Store!.Unlock(Password));
            Password = "";
            shell.Opened(shell.Store!, session);
        }
        catch (WrongPasswordException) { Say("Неверный пароль.", Tone.Error); }
        finally { Busy = false; }
    }
}
