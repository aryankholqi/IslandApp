using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using IslandApp.Core;
using IslandApp.Services;
using RepeatMode = Windows.Media.MediaPlaybackAutoRepeatMode;

namespace IslandApp.Activities;

public interface IMediaController
{
    Task TogglePlayPauseAsync();

    Task SkipNextAsync();

    Task SkipPreviousAsync();

    Task SeekAsync(TimeSpan position);

    /// <summary>Moves a browser's YouTube video into the island, from <paramref name="position"/>.</summary>
    Task WatchInIslandAsync(TimeSpan position);

    Task SetShuffleAsync(bool on);

    Task SetRepeatAsync(RepeatMode mode);

    /// <summary>The player app's own volume (0–1), or null when it can't be controlled.</summary>
    double? GetVolume();

    void SetVolume(double level);
}

/// <summary>Now-playing state. Position is interpolated locally and only ticks while expanded and playing.</summary>
public sealed partial class MediaActivity : IslandActivity, IDisposable
{
    private readonly IMediaController _controller;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private TimeSpan _anchorPosition;
    private DateTimeOffset _anchorTime = DateTimeOffset.Now;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _artist = string.Empty;

    [ObservableProperty]
    private ImageSource? _artwork;

    private Color? _artworkColor;
    private bool _useArtworkColors = true;

    [ObservableProperty]
    private bool _isPlaying;

    /// <summary>Whether the player accepts seeking (Telegram, for one, doesn't).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviousGlyph), nameof(NextGlyph))]
    private bool _canSeek;

    /// <summary>Browser media: the video can be played in the island.</summary>
    [ObservableProperty]
    private bool _canWatchInIsland;

    /// <summary>Whether the player has a next / previous track (a single YouTube video has neither).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NextGlyph))]
    private bool _canSkipNext;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PreviousGlyph))]
    private bool _canSkipPrevious;

    /// <summary>Shuffle and repeat show only when the player accepts them (Spotify's app does; browsers usually don't).</summary>
    [ObservableProperty]
    private bool _canShuffle;

    [ObservableProperty]
    private bool _isShuffleOn;

    [ObservableProperty]
    private bool _canRepeat;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRepeatOn), nameof(RepeatGlyph))]
    private RepeatMode _repeatMode;

    public bool IsRepeatOn => RepeatMode != RepeatMode.None;

    public string RepeatGlyph => RepeatMode == RepeatMode.Track ? "" : ""; // RepeatOne : RepeatAll

    /// <summary>The player app's own volume (0–1); null hides the slider.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasVolume), nameof(VolumeLevel), nameof(CurrentSize))]
    private double? _volume;

    public bool HasVolume => Volume is not null;

    public double VolumeLevel => Volume ?? 0;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress), nameof(RemainingText), nameof(HasTimeline), nameof(CurrentSize), nameof(PreviousGlyph), nameof(NextGlyph))]
    private TimeSpan _duration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Progress), nameof(PositionText), nameof(RemainingText))]
    private TimeSpan _position;

    public MediaActivity(IMediaController controller)
    {
        _controller = controller;
        _ticker.Tick += (_, _) => Position = EstimatePosition();
    }

    public override ActivityPriority Priority => ActivityPriority.Normal;

    public override ActivityTone Tone => ActivityTone.Accent;

    public override Size CompactSize { get; } = new(250, 36);

    // Without a timeline the seek bar row (14 margin + 14 bar) is hidden, so drop its height too.
    public override Size? ExpandedSize => new Size(420, (HasTimeline ? 176 : 148) + (HasVolume ? VolumeRowHeight : 0));

    private const double VolumeRowHeight = 22; // 8 margin + 14 bar

    public override Color? GlowColor => _useArtworkColors ? _artworkColor : null;

    /// <summary>Colour for the waveform and seek bar: the cover's colour, or the theme accent.</summary>
    public Brush AccentBrush { get; private set; } = Brushes.Transparent;

    /// <summary>Tint the glow, waveform and seek bar from the album cover (Island Settings). Setting it also re-reads the theme accent.</summary>
    public bool UseArtworkColors
    {
        get => _useArtworkColors;
        set
        {
            _useArtworkColors = value;
            RefreshColors();
        }
    }

    /// <summary>False when the player reports no timeline (Telegram Desktop sends zeros); the view hides the bar.</summary>
    public bool HasTimeline => Duration > TimeSpan.Zero;

    // With no track to skip to, the skip buttons jump within the track instead (rewind / fast-forward glyphs).
    private static readonly TimeSpan JumpStep = TimeSpan.FromSeconds(10);

    private bool CanJump => CanSeek && HasTimeline;

    public string PreviousGlyph => !CanSkipPrevious && CanJump ? "" : "";

    public string NextGlyph => !CanSkipNext && CanJump ? "" : "";

    public double Progress => Duration > TimeSpan.Zero ? Math.Clamp(Position / Duration, 0, 1) : 0;

    public string PositionText => TimeFormat.Short(Position);

    public string RemainingText => "-" + TimeFormat.Short(Duration - Position);

    public void SyncTimeline(TimeSpan position, TimeSpan duration, DateTimeOffset updatedAt)
    {
        _anchorPosition = position;

        // Some players report a default timestamp; treat that as "now".
        _anchorTime = updatedAt.Year < 2000 ? DateTimeOffset.Now : updatedAt;
        Duration = duration;
        Position = EstimatePosition();
    }

    public void Dispose() => _ticker.Stop();

    partial void OnArtworkChanged(ImageSource? value)
    {
        _artworkColor = ArtworkPalette.DominantColor(value as BitmapSource);
        RefreshColors();
    }

    private void RefreshColors()
    {
        if (_useArtworkColors && _artworkColor is { } color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            AccentBrush = brush;
        }
        else
        {
            AccentBrush = Application.Current?.TryFindResource("AccentBrush") as Brush ?? Brushes.OrangeRed;
        }

        OnPropertyChanged(nameof(GlowColor));
        OnPropertyChanged(nameof(AccentBrush));
    }

    partial void OnIsPlayingChanging(bool value)
    {
        // Re-anchor with the old playing state so the estimate stays continuous.
        _anchorPosition = EstimatePosition();
        _anchorTime = DateTimeOffset.Now;
    }

    partial void OnIsPlayingChanged(bool value) => UpdateTicker();

    protected override void OnExpandedChanged(bool expanded)
    {
        if (expanded)
        {
            Position = EstimatePosition();
            Volume = _controller.GetVolume(); // read on open: the user may have changed it in the player meanwhile
        }

        UpdateTicker();
    }

    private void UpdateTicker() => _ticker.IsEnabled = IsExpanded && IsPlaying;

    private TimeSpan EstimatePosition()
    {
        if (!IsPlaying)
        {
            return _anchorPosition;
        }

        var estimate = _anchorPosition + (DateTimeOffset.Now - _anchorTime);
        return Duration > TimeSpan.Zero && estimate > Duration ? Duration : estimate;
    }

    [RelayCommand]
    private Task PlayPauseAsync() => _controller.TogglePlayPauseAsync();

    [RelayCommand]
    private Task SkipNextAsync() =>
        !CanSkipNext && CanJump ? SeekToAsync(EstimatePosition() + JumpStep) : _controller.SkipNextAsync();

    [RelayCommand]
    private Task SkipPreviousAsync() =>
        !CanSkipPrevious && CanJump ? SeekToAsync(EstimatePosition() - JumpStep) : _controller.SkipPreviousAsync();

    [RelayCommand]
    private Task WatchInIslandAsync() => _controller.WatchInIslandAsync(EstimatePosition());

    [RelayCommand]
    private Task ToggleShuffleAsync() => _controller.SetShuffleAsync(!IsShuffleOn);

    /// <summary>Off → all → one → off, as Spotify cycles it.</summary>
    [RelayCommand]
    private Task CycleRepeatAsync() => _controller.SetRepeatAsync(RepeatMode switch
    {
        RepeatMode.None => RepeatMode.List,
        RepeatMode.List => RepeatMode.Track,
        _ => RepeatMode.None,
    });

    [RelayCommand]
    private void SetVolume(double fraction)
    {
        _controller.SetVolume(fraction);
        Volume = _controller.GetVolume(); // what the app actually took
    }

    [RelayCommand]
    private Task SeekAsync(double fraction) => SeekToAsync(Duration * Math.Clamp(fraction, 0, 1));

    private Task SeekToAsync(TimeSpan target)
    {
        target = target < TimeSpan.Zero ? TimeSpan.Zero : target > Duration ? Duration : target;

        // Move the bar now; the player confirms through a timeline update.
        _anchorPosition = target;
        _anchorTime = DateTimeOffset.Now;
        Position = target;
        return _controller.SeekAsync(target);
    }
}
