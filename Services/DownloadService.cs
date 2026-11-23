using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using IslandApp.Interop;

namespace IslandApp.Services;

/// <summary>
/// Follows Internet Download Manager downloads. IDM has no API, so this reads its download dialog: the title is
/// "4% file.zip" while downloading, and fixed controls hold the exact percentage, speed and time left. Polled,
/// as nothing announces changes to another process's window text.
/// shortcut: control ids and the "N% " title are IDM 6.x's English UI; another version or language may not match.
/// </summary>
public sealed partial class DownloadService : IDisposable
{
    private const int DownloadedId = 1147;  // "13.178  MB  ( 4.60 % )"
    private const int TransferRateId = 1151; // "4.178  MB/sec  "
    private const int TimeLeftId = 1149;     // "1 min 5 sec"
    private const int MainListId = 1002;     // the main window's download list
    private const int StatusColumn = 4, TimeLeftColumn = 5, RateColumn = 6; // "93.24%", "23 sec", "5.66  MB/sec"

    private readonly ActivityCoordinator _coordinator;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private int _misses;

    public DownloadService(ActivityCoordinator coordinator)
    {
        _coordinator = coordinator;
        _poll.Tick += (_, _) => Update();
    }

    public DownloadActivity Activity { get; } = new();

    public void Start()
    {
        SelfCheck();
        Update();
        _poll.Start();
    }

    public void Dispose() => _poll.Stop();

    public void SetPaused(bool paused)
    {
        if (paused)
        {
            _poll.Stop();
        }
        else
        {
            Start();
        }
    }

    private void Update()
    {
        var idm = Process.GetProcessesByName("IDMan");
        var ids = idm.Select(p => p.Id).ToArray();
        foreach (var process in idm)
        {
            process.Dispose();
        }

        // Only a shown dialog is trusted: hidden ones (queued parts, finished parts) keep stale or empty text, and their
        // window order shuffles, which made the ring jump between files. IDM's main list is always current and in a stable order.
        // shortcut: with several downloads, shows one of them; summing them would need each one's size.
        var dialogs = ids.Length > 0 ? NativeMethods.GetDialogs(ids) : [];
        var shown = dialogs.Where(NativeMethods.IsWindowVisible)
            .Select(d => (Dialog: d, Percent: TitlePercent(NativeMethods.GetWindowTitle(d))))
            .Where(d => d.Percent is not null) // skips IDM's main window, finished downloads and errors
            .ToList();

        foreach (var (dialog, percent) in shown)
        {
            if (NativeMethods.GetDescendantText(dialog, TransferRateId) is { } speed && !string.IsNullOrWhiteSpace(speed))
            {
                var exact = ExactPercent(NativeMethods.GetDescendantText(dialog, DownloadedId));
                Show((exact ?? percent!.Value) / 100, speed, NativeMethods.GetDescendantText(dialog, TimeLeftId));
                return;
            }
        }

        if (MainListRow(dialogs) is [var status, var timeLeft, var rate] && TitlePercent(status + " ") is { } listPercent)
        {
            Show(listPercent / 100, rate, timeLeft);
            return;
        }

        if (shown.Count > 0)
        {
            Show(shown[0].Percent!.Value / 100, null, null); // still connecting, or the list couldn't be read
            return;
        }

        // A queue takes a few seconds to go from one part to the next; don't flash the island closed in between.
        if (++_misses >= 4)
        {
            _coordinator.Hide(Activity);
        }
    }

    private void Show(double fraction, string? speed, string? timeLeft)
    {
        _misses = 0;
        Activity.Update(fraction, Speed(speed), Tidy(timeLeft));
        _coordinator.Show(Activity);
    }

    /// <summary>Status, time left and transfer rate of the first downloading row ("93.24%") in IDM's main window list.</summary>
    // shortcut: fixed default column order; a user who reorders IDM's columns gets wrong or empty text — read the header then.
    private static string[]? MainListRow(IEnumerable<IntPtr> dialogs) =>
        dialogs.Select(d => NativeMethods.FindListViewRow(d, MainListId, s => s.EndsWith('%'), StatusColumn, TimeLeftColumn, RateColumn))
            .FirstOrDefault(row => row is not null);

    /// <summary>"4% file.zip" → 4; null when the title isn't a download in progress.</summary>
    internal static double? TitlePercent(string title) =>
        TitleRegex().Match(title) is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>"13.178  MB  ( 4.60 % )" → 4.6.</summary>
    internal static double? ExactPercent(string? text) =>
        text is not null && ExactRegex().Match(text) is { Success: true } m
            ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture)
            : null;

    /// <summary>"4.178  MB/sec  " → "4.178 MB/s".</summary>
    internal static string Speed(string? text) => Tidy(text).Replace("/sec", "/s", StringComparison.Ordinal);

    private static string Tidy(string? text) => SpacesRegex().Replace(text ?? string.Empty, " ").Trim();

    [Conditional("DEBUG")]
    private static void SelfCheck()
    {
        Debug.Assert(TitlePercent("4% test300.dat") == 4);
        Debug.Assert(TitlePercent("Internet Download Manager 6.43") is null);
        Debug.Assert(TitlePercent("test100.dat") is null);
        Debug.Assert(TitlePercent("93.24% ") == 93.24);
        Debug.Assert(ExactPercent("13.178  MB  ( 4.60 % )") == 4.6);
        Debug.Assert(ExactPercent("") is null);
        Debug.Assert(Speed("4.178  MB/sec  ") == "4.178 MB/s");
        Debug.Assert(Tidy(" 1 min  5 sec ") == "1 min 5 sec");
    }

    [GeneratedRegex(@"^(\d+(?:\.\d+)?)% ")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"\(\s*(\d+(?:\.\d+)?)\s*%")]
    private static partial Regex ExactRegex();

    [GeneratedRegex(@"\s+")]
    private static partial Regex SpacesRegex();
}
