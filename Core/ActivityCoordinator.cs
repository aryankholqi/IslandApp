using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IslandApp.Core;

/// <summary>
/// Decides what the island shows. Persistent activities (media, timer) compete by priority;
/// a transient activity (volume, power) briefly overrides them and then expires.
/// Must be used from the UI thread.
/// </summary>
public sealed partial class ActivityCoordinator : ObservableObject
{
    private readonly List<IslandActivity> _persistent = [];
    private readonly DispatcherTimer _transientTimer = new();
    private IslandActivity? _transient;

    [ObservableProperty]
    private IslandActivity? _current;

    /// <summary>Shown in the round bubble beside the island: a companion-capable activity that lost the main slot.</summary>
    [ObservableProperty]
    private IslandActivity? _companion;

    public ActivityCoordinator() => _transientTimer.Tick += OnTransientElapsed;

    /// <summary>Activities this rejects are never shown. Call <see cref="Refresh"/> when its answer changes.</summary>
    public Func<IslandActivity, bool> IsAllowed { get; set; } = _ => true;

    public void Show(IslandActivity activity)
    {
        if (_persistent.Contains(activity))
        {
            return;
        }

        _persistent.Add(activity);
        Refresh();
    }

    public void Hide(IslandActivity activity)
    {
        if (_persistent.Remove(activity) || ClearTransient(activity))
        {
            Refresh();
        }
    }

    /// <summary>Shows <paramref name="activity"/> for <paramref name="duration"/>; repeated calls extend it.</summary>
    public void Flash(IslandActivity activity, TimeSpan duration)
    {
        _transient = activity;
        _transientTimer.Stop();
        _transientTimer.Interval = duration;
        _transientTimer.Start();
        Refresh();
    }

    private bool ClearTransient(IslandActivity activity)
    {
        if (!ReferenceEquals(_transient, activity))
        {
            return false;
        }

        _transientTimer.Stop();
        _transient = null;
        return true;
    }

    private void OnTransientElapsed(object? sender, EventArgs e)
    {
        _transientTimer.Stop();
        _transient = null;
        Refresh();
    }

    public void Refresh()
    {
        var main = HighestPriority();
        var next = _transient is { } transient && IsAllowed(transient) ? transient : main;

        // Excluding "next" too: an activity flashed in the island moves to the bubble only when the flash ends.
        Companion = _persistent
            .Where(a => a.CanBeCompanion && IsAllowed(a) && !ReferenceEquals(a, main) && !ReferenceEquals(a, next))
            .OrderByDescending(a => a.Priority)
            .FirstOrDefault();

        if (ReferenceEquals(next, Current))
        {
            return;
        }

        if (Current is not null)
        {
            Current.IsExpanded = false;
        }

        Current = next;
    }

    private IslandActivity? HighestPriority()
    {
        IslandActivity? best = null;
        foreach (var activity in _persistent)
        {
            if (!IsAllowed(activity) || activity.BubbleOnly)
            {
                continue;
            }

            // ">=" so the most recently shown activity wins ties.
            if (best is null || activity.Priority >= best.Priority)
            {
                best = activity;
            }
        }

        return best;
    }
}
