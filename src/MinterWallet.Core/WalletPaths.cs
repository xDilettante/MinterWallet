namespace MinterWallet.Core;

// Where the wallet keeps its files. A file named "portable" next to the executable keeps everything in "data" beside it
// (a USB stick); otherwise the per-user application data folder of the OS: %LOCALAPPDATA%, ~/.local/share,
// ~/Library/Application Support.
public static class WalletPaths
{
    public static string DataDirectory(string? overrideDirectory = null)
    {
        var directory = overrideDirectory
            ?? (File.Exists(Path.Combine(AppContext.BaseDirectory, "portable"))
                ? Path.Combine(AppContext.BaseDirectory, "data")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "MinterWallet"));
        if (!Directory.Exists(directory))
        {
            if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
            else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return directory;
    }

    // Write to a temporary file in the same folder, flush it to disk, then replace the target keeping the previous
    // version as .bak: a crash leaves either the old or the new file, never a half-written one.
    public static void WriteAtomic(string path, ReadOnlySpan<byte> content, bool secret)
    {
        var temporary = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, Share = FileShare.None };
        if (secret && !OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temporary, options))
        {
            stream.Write(content);
            stream.Flush(flushToDisk: true);
        }
        if (File.Exists(path)) File.Replace(temporary, path, path + ".bak", ignoreMetadataErrors: true);
        else File.Move(temporary, path);
    }
}
