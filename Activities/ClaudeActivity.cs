using System.Globalization;
using System.Windows;
using System.Windows.Threading;
using IslandApp.Core;
using IslandApp.Services;

namespace IslandApp.Activities;

/// <summary>Claude weekly / 5-hour usage. Shown briefly on the island and permanently in Island Settings.</summary>
public sealed class ClaudeActivity : IslandActivity
{
    private ClaudeUsage? _usage;

    public override ActivityPriority Priority => ActivityPriority.High;

    public override ActivityTone Tone => ActivityTone.Accent;

    public override Size CompactSize { get; } = new(300, 44);

    public bool HasData => WeeklyPercent is not null || FiveHourPercent is not null;

    /// <summary>Weekly percentage, or 0 once the reset time has passed (the saved value is from before it).</summary>
    public double? WeeklyPercent => Effective(_usage?.WeeklyPercent, _usage?.WeeklyResetsAt);

    public double? FiveHourPercent => Effective(_usage?.FiveHourPercent, _usage?.FiveHourResetsAt);

    public double WeeklyFraction => Math.Clamp((WeeklyPercent ?? 0) / 100, 0, 1);

    public double FiveHourFraction => Math.Clamp((FiveHourPercent ?? 0) / 100, 0, 1);

    public string WeeklyText => Percent(WeeklyPercent);

    public string FiveHourText => Percent(FiveHourPercent);

    public string WeeklyResetText => ResetText(_usage?.WeeklyResetsAt);

    public string FiveHourResetText => ResetText(_usage?.FiveHourResetsAt);

    /// <summary>Time left until the 5-hour window resets ("2h 14m"), or "5h" when unknown or already reset.</summary>
    public string FiveHourLeftText => _usage?.FiveHourResetsAt - DateTimeOffset.Now is { } left && left > TimeSpan.Zero
        ? left.TotalHours >= 1 ? $"{(int)left.TotalHours}h {left.Minutes}m" : $"{Math.Max(1, left.Minutes)}m"
        : "5h";

    // The countdown moves without new usage data, so refresh it on a clock.
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(30) };

    public ClaudeActivity()
    {
        _ticker.Tick += (_, _) => OnPropertyChanged(nameof(FiveHourLeftText));
        _ticker.Start();
    }

    public string UpdatedText => _usage is { } usage
        ? "Updated " + Ago(DateTimeOffset.Now - usage.UpdatedAt)
        : "No data yet. Send one message in Claude Code.";

    /// <summary>Null clears it (disconnected).</summary>
    public void Update(ClaudeUsage? usage)
    {
        _usage = usage;
        OnPropertyChanged(string.Empty); // every derived property changed
    }

    private static double? Effective(double? percent, DateTimeOffset? resetsAt) =>
        resetsAt is { } at && at <= DateTimeOffset.Now ? 0 : percent;

    private static string Percent(double? value) =>
        value is { } p ? p.ToString("0", CultureInfo.InvariantCulture) + "%" : "–";

    private static string ResetText(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } at)
        {
            return string.Empty;
        }

        var left = at - DateTimeOffset.Now;
        if (left <= TimeSpan.Zero)
        {
            return "Reset since the last update";
        }

        if (left < TimeSpan.FromHours(1))
        {
            return $"Resets in {Math.Max(1, (int)left.TotalMinutes)} min";
        }

        return left < TimeSpan.FromDays(1)
            ? $"Resets in {(int)left.TotalHours} h {left.Minutes} min"
            : "Resets " + at.ToString("ddd HH:mm", CultureInfo.InvariantCulture);
    }

    private static string Ago(TimeSpan age) => age switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalHours: < 1 } => $"{(int)age.TotalMinutes} min ago",
        { TotalDays: < 1 } => $"{(int)age.TotalHours} h ago",
        _ => $"{(int)age.TotalDays} d ago",
    };
}
