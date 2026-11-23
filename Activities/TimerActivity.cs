using System.Media;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IslandApp.Core;

namespace IslandApp.Activities;

/// <summary>Countdown timer. Uses a wall-clock deadline so ticks never drift.</summary>
public sealed partial class TimerActivity : IslandActivity, IDisposable
{
    private readonly ActivityCoordinator _coordinator;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime _deadlineUtc;
    private TimeSpan _total;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingText), nameof(Fraction))]
    private TimeSpan _remaining;

    [ObservableProperty]
    private bool _isRunning;

    public TimerActivity(ActivityCoordinator coordinator)
    {
        _coordinator = coordinator;
        _ticker.Tick += OnTick;
    }

    public override ActivityPriority Priority => ActivityPriority.Timer;

    public override bool CanBeCompanion => true;

    public override ActivityTone Tone => ActivityTone.Accent;

    public override Size CompactSize { get; } = new(230, 36);

    public override Size? ExpandedSize { get; } = new Size(380, 96);

    public string RemainingText => TimeFormat.Short(Remaining);

    /// <summary>Share of the timer left (1 → 0), for the bubble's ring.</summary>
    public double Fraction => _total > TimeSpan.Zero ? Remaining / _total : 0;

    public void Start(TimeSpan duration)
    {
        _total = duration;
        Remaining = duration;
        Resume();
        _coordinator.Show(this);
    }

    public void Dispose() => _ticker.Stop();

    [RelayCommand]
    private void Toggle()
    {
        if (IsRunning)
        {
            Pause();
        }
        else
        {
            Resume();
        }
    }

    [RelayCommand]
    private void Cancel()
    {
        _ticker.Stop();
        IsRunning = false;
        _coordinator.Hide(this);
    }

    private void Resume()
    {
        _deadlineUtc = DateTime.UtcNow + Remaining;
        IsRunning = true;
        _ticker.Start();
    }

    private void Pause()
    {
        _ticker.Stop();
        Remaining = RoundUp(_deadlineUtc - DateTime.UtcNow);
        IsRunning = false;
    }

    private void OnTick(object? sender, EventArgs e)
    {
        var left = _deadlineUtc - DateTime.UtcNow;
        if (left > TimeSpan.Zero)
        {
            // Whole seconds only, so bindings update once per second rather than every tick.
            Remaining = RoundUp(left);
            return;
        }

        Remaining = TimeSpan.Zero;
        SystemSounds.Asterisk.Play();
        Cancel();
    }

    private static TimeSpan RoundUp(TimeSpan value) => TimeSpan.FromSeconds(Math.Ceiling(value.TotalSeconds));
}
