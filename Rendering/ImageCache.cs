using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace MaplePet.Rendering;

/// <summary>
/// Loads small images for the chat/command UI and decodes them to an Avalonia <see cref="Bitmap"/>, cached
/// by URL so each one is fetched once and reused by both the chat-history card and the pet's speech bubble.
/// Two sources: remote http(s) images (the MapleStory character canvas the <c>/rank</c> command shows) and
/// bundled <c>avares://</c> app resources (e.g. the <c>/esfera</c> guide image). Only PERMANENT failures (a
/// 4xx like 404, or any bundled-asset failure) cache as null; transient remote errors (timeout/5xx/
/// connectivity) stay uncached so a later call retries. Bitmaps are small and kept for the process lifetime.
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
    /// Accepts a remote http(s) URL or a bundled <c>avares://</c> app-resource URI. Safe to call repeatedly
    /// and concurrently — calls for the same URL share a single load.</summary>
    public static Task<Bitmap?> LoadAsync(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return Task.FromResult<Bitmap?>(null);
        if (Cache.TryGetValue(url, out var cached)) return Task.FromResult(cached);
        return InFlight.GetOrAdd(url, LoadFactoryAsync);
    }

    /// <summary>True for an Avalonia embedded-resource URI (a bundled app asset, e.g.
    /// <c>avares://MaplePet/Assets/…</c>) as opposed to a remote image. Bundled images are shown whole;
    /// only remote character canvases get center-cropped (see <see cref="CropCharacterCanvas"/>).</summary>
    public static bool IsBundledAsset(string? url)
        => url is not null && url.StartsWith("avares://", StringComparison.OrdinalIgnoreCase);

    /// <summary>Load an image for a speech bubble / history card and prepare it for display: remote character
    /// canvases (from <c>/rank</c>) are center-cropped to drop their wide transparent margin; bundled app
    /// images (from <c>/esfera</c>) are shown whole. Cached like <see cref="LoadAsync"/>; null if unloadable.</summary>
    public static async Task<IImage?> LoadBubbleImageAsync(string? url)
    {
        var bmp = await LoadAsync(url).ConfigureAwait(false);
        return IsBundledAsset(url) ? (IImage?)bmp : CropCharacterCanvas(bmp);
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

    private static async Task<Bitmap?> LoadFactoryAsync(string url)
    {
        try
        {
            // Another caller may have cached this between LoadAsync's check and this factory running.
            if (Cache.TryGetValue(url, out var hit)) return hit;
            Bitmap bmp;
            if (IsBundledAsset(url))
            {
                // Embedded app resource: decoded straight from the bundle, no network.
                using var s = AssetLoader.Open(new Uri(url));
                bmp = new Bitmap(s);
            }
            else
            {
                byte[] bytes = await Http.GetByteArrayAsync(url).ConfigureAwait(false);
                using var ms = new MemoryStream(bytes);
                bmp = new Bitmap(ms);
            }
            Cache[url] = bmp;
            return bmp;
        }
        catch (Exception ex)
        {
            // Negatively-cache only PERMANENT failures, so they don't re-fail for the whole (days-long)
            // process lifetime: a bundled asset that won't load (missing/renamed/corrupt) never will, and a
            // remote 4xx (e.g. 404) won't fix itself. Transient remote errors — timeouts, 5xx, DNS blips, a
            // truncated download — are left UNcached so a later /rank retries.
            if (IsBundledAsset(url) || (ex is HttpRequestException { StatusCode: { } sc } && (int)sc is >= 400 and < 500))
                Cache[url] = null;
            return null;
        }
        finally
        {
            InFlight.TryRemove(url, out _);
        }
    }
}
