using System.Collections.Concurrent;
using Avalonia.Media.Imaging;

namespace MinterWallet;

public static class ImageCache
{
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(5) };

    public static async Task<Bitmap?> LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        if (Cache.TryGetValue(url, out var existing)) return existing;
        try
        {
            var bytes = await Http.GetByteArrayAsync(url);
            using var stream = new MemoryStream(bytes);
            var bmp = new Bitmap(stream);
            Cache[url] = bmp;
            return bmp;
        }
        catch
        {
            Cache[url] = null;
            return null;
        }
    }
}
