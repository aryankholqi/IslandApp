using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace IslandApp.Services;

/// <summary>Reads which page a browser is playing by reading its address bar through UI Automation.</summary>
public static partial class BrowserVideo
{
    /// <summary>
    /// The address (scheme usually omitted, e.g. "anten.ir/live/1") of the browser window whose active tab is titled
    /// like the media, or null. Slow (cross-process): call off the UI thread, which UI Automation also needs because it
    /// queries our own windows too.
    /// </summary>
    public static string? FindTabAddress(string title, string mustContain = "")
    {
        title = title.Trim();
        try
        {
            // A browser window is titled after its active tab: "<page title> - Google Chrome".
            foreach (AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children, Condition.TrueCondition))
            {
                var name = window.Current.Name;
                if ((title.Length > 0 && !name.Contains(title, StringComparison.OrdinalIgnoreCase))
                    || (mustContain.Length > 0 && !name.Contains(mustContain, StringComparison.OrdinalIgnoreCase))
                    || (title.Length == 0 && mustContain.Length == 0))
                {
                    continue;
                }

                // The address bar is the first edit box in the browser's toolbar (before any page content).
                var bar = window.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Edit));
                if (bar?.TryGetCurrentPattern(ValuePattern.Pattern, out var pattern) == true
                    && ((ValuePattern)pattern).Current.Value is { Length: > 0 } address)
                {
                    return address;
                }
            }
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            System.Diagnostics.Debug.WriteLine($"Browser address unavailable: {ex.Message}");
        }

        return null;
    }

    /// <summary>The YouTube video the browser is playing, or null. Slow: off the UI thread.</summary>
    public static string? FindYouTubeVideoId(string title) =>
        ParseVideoId(FindTabAddress(title))
        // The tab title can differ slightly from the media title; then take any window showing YouTube.
        ?? ParseVideoId(FindTabAddress(string.Empty, mustContain: "YouTube"));

    /// <summary>The 11-character id from a watch, youtu.be, shorts, live or embed address (scheme optional).</summary>
    public static string? ParseVideoId(string? address) =>
        address is not null && VideoIdPattern().Match(address) is { Success: true } match ? match.Groups[1].Value : null;

    /// <summary>The host of an address bar value ("anten.ir/x" or "https://www.anten.ir/x" → "www.anten.ir"), or null.</summary>
    public static string? ParseHost(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var withScheme = address.Contains("://", StringComparison.Ordinal) ? address : "https://" + address;
        return Uri.TryCreate(withScheme, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http" && uri.Host.Contains('.')
            ? uri.Host
            : null;
    }

    [GeneratedRegex(@"(?:youtube\.com/.*[?&]v=|youtu\.be/|youtube\.com/(?:shorts|live|embed)/)([\w-]{11})", RegexOptions.IgnoreCase)]
    private static partial Regex VideoIdPattern();
}
