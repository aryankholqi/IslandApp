using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Windows.Automation;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using IslandApp.Interop;

namespace IslandApp.Services;

/// <summary>
/// Follows File Explorer copies and moves through its progress window: the title is "13% complete" (all operations
/// together), and with "More details" open, UI Automation exposes the speed and time remaining.
/// shortcut: matches English Windows text; another display language shows the ring without speed and time.
/// </summary>
public sealed partial class FileCopyService : IDisposable
{
    private readonly ActivityCoordinator _coordinator;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _reading;

    public FileCopyService(ActivityCoordinator coordinator)
    {
        _coordinator = coordinator;
        _poll.Tick += async (_, _) => await UpdateAsync();
    }

    /// <summary>Same look as an IDM download: a ring in the bubble, speed and time left when widened.</summary>
    public DownloadActivity Activity { get; } = new();

    public void Start()
    {
        SelfCheck();
        _poll.Start();
    }

    public void Dispose() => _poll.Stop();

    public void SetPaused(bool paused) => _poll.IsEnabled = !paused;

    private async Task UpdateAsync()
    {
        if (_reading)
        {
            return; // a slow UI Automation read is still running
        }

        var window = NativeMethods.FindWindowByClass("OperationStatusWindow");
        if (window == IntPtr.Zero || Percent(NativeMethods.GetWindowTitle(window)) is not { } percent)
        {
            _coordinator.Hide(Activity);
            return;
        }

        _reading = true;
        try
        {
            // UI Automation calls into Explorer; kept off the UI thread so a busy Explorer can't freeze the island.
            var (speed, timeLeft) = await Task.Run(() => ReadDetails(window));
            Activity.Update(percent / 100, speed, timeLeft);
            _coordinator.Show(Activity);
        }
        finally
        {
            _reading = false;
        }
    }

    private static (string Speed, string TimeLeft) ReadDetails(IntPtr window)
    {
        try
        {
            var root = AutomationElement.FromHandle(window);
            return (Speed(NameOf(root, "eltRateChart_New")), Tidy(NameOf(root, "eltTimeRemaining")));
        }
        catch (Exception ex) when (ex is ElementNotAvailableException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Debug.WriteLine($"Copy details unavailable: {ex.Message}");
            return (string.Empty, string.Empty);
        }
    }

    private static string? NameOf(AutomationElement root, string automationId) =>
        root.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.AutomationIdProperty, automationId))?.Current.Name;

    /// <summary>"13% complete" or "Paused - 13% complete" → 13; null for other titles (e.g. "Replace or Skip Files").</summary>
    internal static double? Percent(string title) =>
        PercentRegex().Match(title) is { Success: true } m ? double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : null;

    /// <summary>"Operation progress: 7%; Speed: 488 MB per second" → "488 MB/s".</summary>
    internal static string Speed(string? chart) =>
        chart is not null && SpeedRegex().Match(chart) is { Success: true } m ? m.Groups[1].Value + "/s" : string.Empty;

    /// <summary>"Calculating..." isn't a time; anything else ("About 30 seconds") is shown as is.</summary>
    private static string Tidy(string? text) => text is null || text.StartsWith("Calculating", StringComparison.Ordinal) ? string.Empty : text.Trim();

    [Conditional("DEBUG")]
    private static void SelfCheck()
    {
        Debug.Assert(Percent("11% complete") == 11);
        Debug.Assert(Percent("Paused - 13% complete") == 13);
        Debug.Assert(Percent("Replace or Skip Files") is null);
        Debug.Assert(Speed("Operation progress: 7%; Speed: 488 MB per second") == "488 MB/s");
        Debug.Assert(Speed("Operation progress: 7%") == string.Empty);
        Debug.Assert(Tidy("Calculating...") == string.Empty);
    }

    [GeneratedRegex(@"(\d+)% complete")]
    private static partial Regex PercentRegex();

    [GeneratedRegex(@"Speed:\s*(.+?)\s+per second")]
    private static partial Regex SpeedRegex();
}
