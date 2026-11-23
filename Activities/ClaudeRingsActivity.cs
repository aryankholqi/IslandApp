using System.Windows;
using IslandApp.Core;

namespace IslandApp.Activities;

/// <summary>
/// Claude usage as two rings (weekly outer/orange, 5-hour white), shown while an IDE is focused.
/// Lowest priority: when media or a timer takes the island it moves to the side bubble instead.
/// </summary>
public sealed class ClaudeRingsActivity(ClaudeActivity usage) : IslandActivity
{
    public ClaudeActivity Usage { get; } = usage;

    public override ActivityPriority Priority => ActivityPriority.Low;

    public override bool CanBeCompanion => true;

    public override Size CompactSize { get; } = new(236, 36);
}
