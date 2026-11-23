using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace IslandApp.Core;

/// <summary>
/// Persistent activities compete for the island by this order; the next companion-capable one takes the side bubble.
/// Media outranks the rest so music keeps the island while a timer, call or battery ring moves to the bubble.
/// </summary>
public enum ActivityPriority
{
    Low = -10,      // Claude usage rings
    Battery = -8,
    Timer = -5,
    Shelf = -4,     // files dropped on the island
    Download = -3,  // IDM download; below capture so privacy wins the bubble
    Capture = -2,   // microphone / camera / screen capture in use
    Normal = 0,     // media
    High = 10,
}

/// <summary>Semantic colour of an activity; the view maps it to the glow colour.</summary>
public enum ActivityTone
{
    Neutral,
    Accent,
    Positive,
}

/// <summary>Base view-model for anything the island can display.</summary>
public abstract partial class IslandActivity : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CurrentSize))]
    private bool _isExpanded;

    public abstract ActivityPriority Priority { get; }

    public virtual ActivityTone Tone => ActivityTone.Neutral;

    /// <summary>Overrides the tone's glow colour when set (e.g. taken from album artwork).</summary>
    public virtual Color? GlowColor => null;

    /// <summary>Island size (DIPs) in the compact state.</summary>
    public abstract Size CompactSize { get; }

    /// <summary>Island size (DIPs) when expanded, or <c>null</c> if the activity can't expand.</summary>
    public virtual Size? ExpandedSize => null;

    public bool CanExpand => ExpandedSize is not null;

    /// <summary>When another persistent activity takes the island, this one moves to the side bubble instead of hiding.</summary>
    public virtual bool CanBeCompanion => false;

    /// <summary>Never takes the island as a persistent activity; lives in the bubble (it can still be flashed in the island).</summary>
    public virtual bool BubbleOnly => false;

    /// <summary>Width (DIPs) of the closed side bubble: a circle, unless the activity needs a short pill.</summary>
    public virtual double BubbleWidth => 36;

    public Size CurrentSize => IsExpanded && ExpandedSize is { } expanded ? expanded : CompactSize;

    partial void OnIsExpandedChanged(bool value) => OnExpandedChanged(value);

    protected virtual void OnExpandedChanged(bool expanded)
    {
    }
}
