using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace MaplePet.Rendering;

/// <summary>
/// Downloads small remote images (the MapleStory character canvas the <c>/rank</c> command shows) and
/// decodes them to an Avalonia <see cref="Bitmap"/>, cached by URL so the same avatar is fetched once and
/// reused by both the chat-history card and the pet's speech bubble. Only PERMANENT failures (a 4xx like
/// 404) cache as null; transient errors (timeout/5xx/connectivity) stay uncached so a later call retries.
/// Bitmaps are small (e.g. 96×96 PNGs) and kept for the process lifetime.
/// </summary>
public static class ImageCache
{
    private static readonly HttpClient Http = CreateHttp();
    private static readonly ConcurrentDictionary<string, Bitmap?> Cache = new();
    private static readonly ConcurrentDictionary<string, Task<Bitmap?>> InFlight = new();

    private static HttpClient CreateHttp()
    {
        var c = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        c.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) MaplePet");
        return c;
    }

    /// <summary>Return the decoded bitmap for <paramref name="url"/> (cached), or null if it can't be loaded.
    /// Safe to call repeatedly and concurrently — calls for the same URL share a single download.</summary>
    public static Task<Bitmap?> LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<Bitmap?>(null);
        if (Cache.TryGetValue(url, out var cached)) return Task.FromResult(cached);
        return InFlight.GetOrAdd(url, DownloadAsync);
    }

    /// <summary>The center square (in px) a character render is cropped to. Both the GMS and Open API canvases
    /// surround the figure with a wide transparent margin; the character itself sits in roughly this box.</summary>
    private const int CanvasCrop = 100;

    /// <summary>Trim a character render to its center <see cref="CanvasCrop"/>×<see cref="CanvasCrop"/> region,
    /// dropping the transparent margin around the figure. Returns a lightweight <see cref="CroppedBitmap"/>
    /// view (no pixel copy), the source unchanged when it's already within the crop box, or null.</summary>
    public static IImage? CropCharacterCanvas(Bitmap? src)
    {
        if (src is null) return null;
        int w = src.PixelSize.Width, h = src.PixelSize.Height;
        int cw = Math.Min(CanvasCrop, w), ch = Math.Min(CanvasCrop, h);
        if (cw <= 0 || ch <= 0 || (cw == w && ch == h)) return src; // nothing to trim
        return new CroppedBitmap(src, new PixelRect((w - cw) / 2, (h - ch) / 2, cw, ch));
    }

    private static async Task<Bitmap?> DownloadAsync(string url)
    {
        try
        {
            // Another caller may have cached this between LoadAsync's check and this factory running.
            if (Cache.TryGetValue(url, out var hit)) return hit;
            byte[] bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
            using var ms = new MemoryStream(bytes);
            var bmp = new Bitmap(ms);
            Cache[url] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            // Only negatively-cache PERMANENT failures (a 4xx such as 404 won't fix itself). Transient errors
            // — timeouts, 5xx, DNS/connectivity blips, a truncated download — are left UNcached so a later
            // /rank retries, instead of the avatar being dead for the whole (days-long) process lifetime.
            if (ex is HttpRequestException { StatusCode: { } sc } && (int)sc is >= 400 and < 500)
                Cache[url] = null;
            return null;
        }
        finally
        {
            InFlight.TryRemove(url, out _);
        }
    }
}
