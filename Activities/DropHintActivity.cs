using System.Windows;
using IslandApp.Core;

namespace IslandApp.Activities;

/// <summary>Shown while files are dragged over the island: it opens a little to say "drop here".</summary>
public sealed class DropHintActivity : IslandActivity
{
    public override ActivityPriority Priority => ActivityPriority.High;

    public override ActivityTone Tone => ActivityTone.Accent;

    public override Size CompactSize { get; } = new(170, 36);
}
