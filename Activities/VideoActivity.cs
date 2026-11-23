using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IslandApp.Core;

namespace IslandApp.Activities;

/// <summary>
/// A browser video playing in the island: a YouTube video (through YouTube's player) or any other site's page with
/// its video stretched to fill the island. The video itself is a separate window (IslandWindow's VideoWindow) laid
/// over <see cref="VideoRect"/>, because WebView2 can't render in the island's transparent window. Stays open (and
/// audible) while a notification briefly takes the island.
/// </summary>
public sealed partial class VideoActivity(ActivityCoordinator coordinator) : IslandActivity
{
    /// <summary>Where the video sits inside the island (DIPs): edge to edge, 16:9. Title and close are drawn on the video.</summary>
    public static readonly Rect VideoRect = new(0, 0, 368, 207);

    /// <summary>A YouTube video id, or a page address (https://…). Changes once per opened video; the view (re)loads on change.</summary>
    [ObservableProperty]
    private string? _source;

    public bool IsYouTube { get; private set; }

    public TimeSpan Start { get; private set; }

    /// <summary>Asks the player for its position; set by the view. Null when it can't tell (or it's a live stream).</summary>
    public Func<Task<TimeSpan?>>? GetPosition { get; set; }

    /// <summary>Raised after closing, with where the video got to, so the browser can carry on from there.</summary>
    public event Action<TimeSpan?>? Closed;

    public bool IsOpen => Source is not null;

    public override ActivityPriority Priority => ActivityPriority.High;

    public override ActivityTone Tone => ActivityTone.Accent;

    public override Size CompactSize { get; } = new(VideoRect.Right + VideoRect.Left, VideoRect.Bottom + VideoRect.Left);

    public void Open(string source, bool isYouTube, TimeSpan start)
    {
        IsYouTube = isYouTube;
        Start = start;
        Source = source;
        OnPropertyChanged(nameof(IsOpen));
        coordinator.Show(this);
    }

    [RelayCommand]
    private async Task CloseAsync()
    {
        var position = GetPosition is { } get ? await get() : null;
        Source = null;
        OnPropertyChanged(nameof(IsOpen));
        coordinator.Hide(this);
        Closed?.Invoke(position);
    }
}
