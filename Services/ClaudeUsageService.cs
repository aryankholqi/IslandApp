using System.IO;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;

namespace IslandApp.Services;

/// <summary>
/// Watches the usage file written by the Claude Code status line bridge (<see cref="ClaudeCode"/>) and
/// flashes the island each time weekly usage crosses another <see cref="MilestoneStep"/>%, so it stays quiet while you work.
/// </summary>
public sealed class ClaudeUsageService : IDisposable
{

    private readonly ActivityCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private FileSystemWatcher? _watcher;

    public ClaudeUsageService(ActivityCoordinator coordinator, Dispatcher dispatcher)
    {
        _coordinator = coordinator;
        _dispatcher = dispatcher;
    }

    /// <summary>How long the island shows this activity (Island Settings).</summary>
    public TimeSpan DisplayDuration { get; set; } = TimeSpan.FromSeconds(4);

    public ClaudeActivity Activity { get; } = new();

    /// <summary>Flash each time weekly usage crosses another multiple of this many percent; 0 = never.</summary>
    public int MilestoneStep { get; set; } = 10;

    public void Start()
    {
        Load(flashOnMilestone: false);

        var folder = Path.GetDirectoryName(ClaudeCode.UsageFile)!;
        Directory.CreateDirectory(folder);
        _watcher = new FileSystemWatcher(folder, Path.GetFileName(ClaudeCode.UsageFile))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName,
        };

        // The bridge swaps the file in with a rename; Changed covers editors and other writers.
        _watcher.Created += OnUsageFileChanged;
        _watcher.Changed += OnUsageFileChanged;
        _watcher.Renamed += OnUsageFileChanged;
        _watcher.Deleted += OnUsageFileChanged;
        _watcher.EnableRaisingEvents = true;
    }

    /// <summary>Shows current usage on the island on demand (right-click menu).</summary>
    public void Show()
    {
        Load(flashOnMilestone: false);
        _coordinator.Flash(Activity, DisplayDuration);
    }

    public void Dispose() => _watcher?.Dispose();

    // Called on a thread-pool thread.
    private void OnUsageFileChanged(object sender, FileSystemEventArgs e) =>
        _dispatcher.InvokeAsync(() => Load(flashOnMilestone: true));

    private void Load(bool flashOnMilestone)
    {
        if (ClaudeCode.ReadUsage() is not { } usage)
        {
            // Deleted on disconnect: drop the stale numbers. Otherwise it's mid-replace; the next event rereads it.
            if (!File.Exists(ClaudeCode.UsageFile))
            {
                Activity.Update(null);
            }

            return;
        }

        var before = Activity.WeeklyPercent;
        Activity.Update(usage);

        if (flashOnMilestone && MilestoneStep > 0 && Activity.WeeklyPercent is { } now
            && (before is null || Math.Floor(now / MilestoneStep) > Math.Floor(before.Value / MilestoneStep)))
        {
            _coordinator.Flash(Activity, DisplayDuration);
        }
    }
}
