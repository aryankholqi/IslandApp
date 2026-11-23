using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Storage.Streams;

namespace IslandApp.Services;

internal static class ArtworkLoader
{
    // Largest artwork on the island is 56 DIPs; 2x covers 200% displays without decoding full-size covers.
    private const int DecodePixelSize = 112;

    private static BitmapSource? _youTubeLogo;

    /// <summary>
    /// Shown for browser media instead of the browser's artwork (Chromium browsers send their own logo).
    /// A bitmap, not a drawing, so <see cref="ArtworkPalette"/> can read its red for the glow. Create on the UI thread.
    /// </summary>
    public static BitmapSource YouTubeLogo => _youTubeLogo ??= DrawYouTubeLogo();

    private static BitmapSource DrawYouTubeLogo()
    {
        const int size = DecodePixelSize;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x21, 0x21, 0x21)), null, new Rect(0, 0, size, size));
            dc.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xFF, 0x00, 0x00)), null, new Rect(16, 32, 80, 56), 16, 16);
            var play = new StreamGeometry();
            using (var g = play.Open())
            {
                g.BeginFigure(new Point(48, 47), isFilled: true, isClosed: true);
                g.LineTo(new Point(48, 73), isStroked: false, isSmoothJoin: false);
                g.LineTo(new Point(70, 60), isStroked: false, isSmoothJoin: false);
            }

            dc.DrawGeometry(Brushes.White, null, play);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    public static async Task<ImageSource?> LoadAsync(IRandomAccessStreamReference? reference)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var winRtStream = await reference.OpenReadAsync();
            using var stream = winRtStream.AsStreamForRead();

            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = DecodePixelSize;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex) when (ex is IOException or COMException or NotSupportedException or FileFormatException)
        {
            Debug.WriteLine($"Artwork could not be decoded: {ex.Message}");
            return null;
        }
    }
}

/// <summary>
/// A website's own icon, for browser media (browsers send their own logo, not the site's). Asks only the site itself:
/// the icons its homepage lists (apple-touch-icon first, the large ones), then /favicon.ico. Cached per host.
/// </summary>
internal static partial class SiteIcon
{
    private const int MaxPixels = 112; // same as ArtworkLoader's decode size
    private const int MaxBytes = 2_000_000;

    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, Task<BitmapSource?>> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Null when the site has no icon we can decode (e.g. SVG only) or can't be reached.</summary>
    public static Task<BitmapSource?> LoadAsync(string host)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(host, out var task))
            {
                Cache[host] = task = Task.Run(() => FetchAsync(host));
            }

            return task;
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0 Safari/537.36");
        return client;
    }

    private static async Task<BitmapSource?> FetchAsync(string host)
    {
        var site = new Uri($"https://{host}/");
        var candidates = new List<Uri>();
        try
        {
            var html = await Http.GetStringAsync(site);
            candidates.AddRange(LinkTagPattern().Matches(html)
                .Select(m => (Tag: m.Value, Rel: AttributePattern("rel").Match(m.Value), Href: AttributePattern("href").Match(m.Value)))
                .Where(l => l.Rel.Success && l.Href.Success && l.Rel.Groups[1].Value.Contains("icon", StringComparison.OrdinalIgnoreCase))
                .OrderBy(l => l.Rel.Groups[1].Value.Contains("apple", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(l => Size(l.Tag))
                .Select(l => Uri.TryCreate(site, WebUtility.HtmlDecode(l.Href.Groups[1].Value), out var uri) ? uri : null)
                .OfType<Uri>()
                .Where(uri => uri.Scheme is "https" or "http" && !uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            Debug.WriteLine($"Site page unavailable for {host}: {ex.Message}");
        }

        candidates.Add(new Uri(site, "/favicon.ico"));
        foreach (var uri in candidates.Distinct().Take(6))
        {
            if (await TryDecodeAsync(uri) is { } icon)
            {
                return icon;
            }
        }

        return null;
    }

    private static async Task<BitmapSource?> TryDecodeAsync(Uri uri)
    {
        try
        {
            using var response = await Http.GetAsync(uri);
            // Single-page sites answer any path with their HTML page (status 200), so check it's an image.
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
                || response.Content.Headers.ContentLength > MaxBytes)
            {
                return null;
            }

            var bytes = await response.Content.ReadAsByteArrayAsync();
            var decoder = BitmapDecoder.Create(new MemoryStream(bytes), BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            BitmapSource frame = decoder.Frames.OrderByDescending(f => f.PixelWidth).First(); // .ico holds several sizes
            if (frame.PixelWidth > MaxPixels)
            {
                var scale = (double)MaxPixels / frame.PixelWidth;
                frame = new TransformedBitmap(frame, new ScaleTransform(scale, scale));
            }

            frame.Freeze();
            return frame;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or NotSupportedException or FileFormatException or ArgumentException or COMException or InvalidOperationException)
        {
            Debug.WriteLine($"Site icon {uri} unusable: {ex.Message}");
            return null;
        }
    }

    // "sizes=\"180x180\"" → 180; 0 when absent.
    private static int Size(string tag) =>
        AttributePattern("sizes").Match(tag) is { Success: true } m && int.TryParse(m.Groups[1].Value.Split('x')[0], out var size) ? size : 0;

    private static Regex AttributePattern(string name) =>
        new($@"\b{name}\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase);

    [GeneratedRegex(@"<link\b[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex LinkTagPattern();
}

internal static class ArtworkPalette
{
    private const int HueBuckets = 36;

    /// <summary>
    /// Most prominent colour of the cover, brightened for use as a glow. Prefers the dominant vivid hue;
    /// muted or greyscale covers fall back to their average colour. Null only when there is no artwork.
    /// </summary>
    public static Color? DominantColor(BitmapSource? source)
    {
        if (source is null || source.PixelWidth == 0 || source.PixelHeight == 0)
        {
            return null;
        }

        // Analyse only the centred square the island shows (UniformToFill). Telegram pads covers into
        // 16:9 with solid side bars, which would otherwise outvote the cover itself.
        var side = Math.Min(source.PixelWidth, source.PixelHeight);
        var square = new CroppedBitmap(source, new Int32Rect((source.PixelWidth - side) / 2, (source.PixelHeight - side) / 2, side, side));
        var bitmap = new FormatConvertedBitmap(square, PixelFormats.Bgra32, null, 0);
        var stride = bitmap.PixelWidth * 4;
        var pixels = new byte[stride * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, stride, 0);

        var weights = new double[HueBuckets];
        var sums = new double[HueBuckets, 3];
        double allR = 0, allG = 0, allB = 0, allCount = 0;
        for (var i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i + 3] < 128)
            {
                continue;
            }

            double b = pixels[i], g = pixels[i + 1], r = pixels[i + 2];
            allR += r;
            allG += g;
            allB += b;
            allCount++;

            var max = Math.Max(r, Math.Max(g, b));
            var delta = max - Math.Min(r, Math.Min(g, b));
            var saturation = max == 0 ? 0 : delta / max;
            var value = max / 255;
            if (saturation < 0.15 || value < 0.12)
            {
                continue;
            }

            var hue = max == r ? ((g - b) / delta + 6) % 6
                : max == g ? ((b - r) / delta) + 2
                : ((r - g) / delta) + 4;
            var bucket = Math.Min(HueBuckets - 1, (int)(hue / 6 * HueBuckets));

            // Saturation counts more than brightness so a few bright grey highlights can't win.
            var weight = saturation * saturation * value;
            weights[bucket] += weight;
            sums[bucket, 0] += r * weight;
            sums[bucket, 1] += g * weight;
            sums[bucket, 2] += b * weight;
        }

        if (allCount == 0)
        {
            return null;
        }

        // Score each bucket with its neighbours (wrapping at red) so a hue split across two buckets isn't penalised.
        int best = 0;
        double bestScore = -1;
        for (var i = 0; i < HueBuckets; i++)
        {
            var score = weights[(i + HueBuckets - 1) % HueBuckets] + weights[i] + weights[(i + 1) % HueBuckets];
            if (score > bestScore)
            {
                best = i;
                bestScore = score;
            }
        }

        // Vivid hue needs at least ~0.5% of the cover at moderate saturation; otherwise use the average colour.
        if (bestScore >= allCount * 0.005 * 0.25)
        {
            double rr = 0, gg = 0, bb = 0, w = 0;
            foreach (var i in new[] { best - 1, best, best + 1 })
            {
                var k = (i + HueBuckets) % HueBuckets;
                rr += sums[k, 0];
                gg += sums[k, 1];
                bb += sums[k, 2];
                w += weights[k];
            }

            return Brighten(rr / w, gg / w, bb / w);
        }

        return Brighten(allR / allCount, allG / allCount, allB / allCount);
    }

    /// <summary>
    /// True for a round logo on a transparent square: what a browser sends when the page gives no artwork (its own
    /// icon). Real covers are opaque to the corners.
    /// </summary>
    public static bool HasTransparentCorners(BitmapSource source)
    {
        if (source.PixelWidth < 2 || source.PixelHeight < 2)
        {
            return false;
        }

        var bitmap = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var pixel = new byte[4];
        var transparent = 0;
        foreach (var (x, y) in new[] { (0, 0), (bitmap.PixelWidth - 1, 0), (0, bitmap.PixelHeight - 1), (bitmap.PixelWidth - 1, bitmap.PixelHeight - 1) })
        {
            bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
            if (pixel[3] < 128)
            {
                transparent++;
            }
        }

        return transparent >= 3;
    }

    // Full brightness so the glow reads on the near-black pill.
    private static Color Brighten(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        if (max < 1)
        {
            return Colors.White;
        }

        var scale = 255 / max;
        return Color.FromRgb((byte)(r * scale), (byte)(g * scale), (byte)(b * scale));
    }
}
