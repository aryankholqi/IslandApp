using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace IslandApp.Controls;

/// <summary>
/// <c>c:Emoji.Text</c> on a TextBlock: like <c>Text</c>, but emoji are drawn as Noto Color Emoji images,
/// because WPF can't render colour fonts. Emoji missing from the set stay plain text.
/// Images come from Assets/emoji.zip, keyed by lowercase codepoints without FE0F ("1f44d-1f3fd.png").
/// </summary>
public static class Emoji
{
    public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
        "Text", typeof(string), typeof(Emoji), new PropertyMetadata(null, OnTextChanged));

    private const int VariationSelector = 0xFE0F;

    private static readonly Lazy<ZipArchive?> Archive = new(OpenArchive);
    private static readonly Dictionary<string, ImageSource?> Cache = [];
    private static readonly Transform EmojiScale = Frozen(new ScaleTransform(1.2, 1.2));

    public static string? GetText(TextBlock element) => (string?)element.GetValue(TextProperty);

    public static void SetText(TextBlock element, string? value) => element.SetValue(TextProperty, value);

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var block = (TextBlock)d;
        block.Inlines.Clear();

        var pending = new StringBuilder();
        var elements = StringInfo.GetTextElementEnumerator((string?)e.NewValue ?? string.Empty);
        while (elements.MoveNext())
        {
            // Grapheme clusters keep ZWJ sequences, skin tones and keycaps together.
            var element = elements.GetTextElement();
            if (Lookup(element) is not { } source)
            {
                pending.Append(element);
                continue;
            }

            Flush(block, pending);
            var image = new Image { Source = source, LayoutTransform = EmojiScale };
            image.SetBinding(FrameworkElement.HeightProperty, new Binding(nameof(TextBlock.FontSize)) { Source = block });
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            block.Inlines.Add(new InlineUIContainer(image) { BaselineAlignment = BaselineAlignment.Center });
        }

        Flush(block, pending);
    }

    private static void Flush(TextBlock block, StringBuilder pending)
    {
        if (pending.Length > 0)
        {
            block.Inlines.Add(new Run(pending.ToString()));
            pending.Clear();
        }
    }

    private static ImageSource? Lookup(string element)
    {
        // Fast path: plain letters, digits and punctuation are never looked up.
        if (element.Length == 1 && element[0] < 0x2000)
        {
            return null;
        }

        var key = Key(element);
        if (Cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        return Cache[key] = Archive.Value?.GetEntry(key + ".png") is { } entry ? Decode(entry) : null;
    }

    private static string Key(string element)
    {
        var parts = new List<string>();
        for (var i = 0; i < element.Length; i += char.IsSurrogatePair(element, i) ? 2 : 1)
        {
            var codepoint = char.ConvertToUtf32(element, i);
            if (codepoint != VariationSelector)
            {
                parts.Add(codepoint.ToString("x", CultureInfo.InvariantCulture));
            }
        }

        return string.Join('-', parts);
    }

    private static BitmapImage? Decode(ZipArchiveEntry entry)
    {
        try
        {
            var buffer = new MemoryStream();
            using (var stream = entry.Open())
            {
                stream.CopyTo(buffer);
            }

            buffer.Position = 0;
            var image = new BitmapImage();
            image.BeginInit();
            image.StreamSource = buffer;
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or NotSupportedException)
        {
            System.Diagnostics.Debug.WriteLine($"Emoji {entry.Name} unreadable: {ex.Message}");
            return null;
        }
    }

    private static ZipArchive? OpenArchive()
    {
        try
        {
            // Stays open for the app's lifetime; entries are read on demand and cached.
            return ZipFile.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assets", "emoji.zip"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            System.Diagnostics.Debug.WriteLine($"Emoji set unavailable, using plain text: {ex.Message}");
            return null;
        }
    }

    private static Transform Frozen(Transform transform)
    {
        transform.Freeze();
        return transform;
    }
}
