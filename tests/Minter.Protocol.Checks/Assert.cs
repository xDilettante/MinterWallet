using System.Text.Json;

namespace Minter.Protocol.Checks;

internal static class Assert
{
    public static void True(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
    public static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{what}: expected {expected}, got {actual}");
    }
    public static void Throws<TException>(Action action, string what) where TException : Exception
    {
        try { action(); }
        catch (TException) { return; }
        throw new InvalidOperationException($"{what}: expected {typeof(TException).Name}");
    }
}

internal static class Fixtures
{
    private static readonly string Directory = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    public static JsonElement Load(string name) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, name))).RootElement;
    public static JsonElement Cases => Load("cases.json");
    public static JsonElement Go => Load("go-expected.json");
    public static JsonElement Js => Load("js-expected.json");
}
