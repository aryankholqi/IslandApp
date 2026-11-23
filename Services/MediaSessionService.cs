using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using Windows.Media.Control;
using Windows.Storage.Streams;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using SessionManager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;

namespace IslandApp.Services;

/// <summary>
/// Mirrors the system "now playing" session (Spotify, browsers, Media Player…) into a <see cref="MediaActivity"/>.
/// WinRT events arrive on background threads and are marshalled to the UI thread before touching state.
/// </summary>
public sealed class MediaSessionService : IMediaController, IDisposable
{
    private readonly ActivityCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private SessionManager? _manager;
    private Session? _session;
    private IReadOnlyList<Session> _watched = [];
    private PlaybackStatus _status = PlaybackStatus.Closed;
    private int _propertiesVersion;
    private (string App, string? Title, string? Artist, System.Windows.Media.ImageSource? Artwork)? _lastKnown;
    private TimeSpan _timelineStart;
    private readonly DispatcherTimer _hideDelay = new() { Interval = TimeSpan.FromSeconds(1.5) };

    private readonly VideoActivity _video;

    public MediaSessionService(ActivityCoordinator coordinator, Dispatcher dispatcher, VideoActivity video)
    {
        _coordinator = coordinator;
        _dispatcher = dispatcher;
        _video = video;
        Activity = new MediaActivity(this);
        _hideDelay.Tick += OnHideDelayElapsed;
        video.Closed += OnVideoClosed;
    }

    public async Task WatchInIslandAsync(TimeSpan position)
    {
        var title = Activity.Title;
        var (source, isYouTube) = await Task.Run<(string?, bool)>(() =>
        {
            var address = BrowserVideo.FindTabAddress(title);
            if (BrowserVideo.ParseVideoId(address) is { } id)
            {
                return (id, true);
            }

            // Any other site: open the page itself (the address bar usually leaves out "https://").
            if (address is not null && BrowserVideo.ParseHost(address) is not null)
            {
                return (address.Contains("://", StringComparison.Ordinal) ? address : "https://" + address, false);
            }

            // No tab titled like the media: any window showing YouTube.
            return (BrowserVideo.FindYouTubeVideoId(title), true);
        });
        if (source is null || _session is not { } session)
        {
            return; // shortcut: no feedback when the video's tab isn't the active one; add a hint if users get stuck
        }

        try
        {
            // The running estimate drifts; the browser reports its exact position when it pauses, so wait for that.
            var before = session.GetTimelineProperties().LastUpdatedTime;
            await session.TryPauseAsync();
            for (var i = 0; i < 10; i++)
            {
                await Task.Delay(100);
                var timeline = session.GetTimelineProperties();
                if (timeline.LastUpdatedTime != before)
                {
                    position = timeline.Position - timeline.StartTime;
                    break;
                }
            }
        }
        catch (COMException)
        {
            // Session closed meanwhile; the island still plays from the estimate.
        }

        _video.Open(source, isYouTube, position);
    }

    /// <summary>Carries on in the browser from where the island got to.</summary>
    private async void OnVideoClosed(TimeSpan? position)
    {
        if (_session is not { } session)
        {
            return;
        }

        try
        {
            // Only when the browser's own timeline fits it: a live stream or a pre-roll ad in the island has a position
            // that means nothing to the browser's video.
            if (position is { } at && Activity.CanSeek && Activity.HasTimeline && at <= Activity.Duration)
            {
                await session.TryChangePlaybackPositionAsync((_timelineStart + at).Ticks);
            }

            await session.TryPlayAsync();
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Could not resume in the browser: {ex.Message}");
        }
    }

    public MediaActivity Activity { get; }

    public async Task StartAsync()
    {
        _manager = await SessionManager.RequestAsync();
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        _manager.SessionsChanged += OnSessionsChanged;
        WatchSessions();
    }

    public Task TogglePlayPauseAsync() => _session?.TryTogglePlayPauseAsync().AsTask() ?? Task.CompletedTask;

    public Task SkipNextAsync() => _session?.TrySkipNextAsync().AsTask() ?? Task.CompletedTask;

    public Task SkipPreviousAsync() => _session?.TrySkipPreviousAsync().AsTask() ?? Task.CompletedTask;

    public Task SeekAsync(TimeSpan position) =>
        _session?.TryChangePlaybackPositionAsync((_timelineStart + position).Ticks).AsTask() ?? Task.CompletedTask;

    public Task SetShuffleAsync(bool on) => _session?.TryChangeShuffleActiveAsync(on).AsTask() ?? Task.CompletedTask;

    public Task SetRepeatAsync(Windows.Media.MediaPlaybackAutoRepeatMode mode) =>
        _session?.TryChangeAutoRepeatModeAsync(mode).AsTask() ?? Task.CompletedTask;

    public double? GetVolume() => _session is { } session ? AppVolume.Get(session.SourceAppUserModelId) : null;

    public void SetVolume(double level)
    {
        if (_session is { } session)
        {
            AppVolume.Set(session.SourceAppUserModelId, (float)level);
        }
    }

    public void Dispose()
    {
        if (_manager is not null)
        {
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
            _manager.SessionsChanged -= OnSessionsChanged;
            _manager = null;
        }

        _hideDelay.Stop();
        UnwatchSessions();
        Detach();
        Activity.Dispose();
    }

    private void OnCurrentSessionChanged(SessionManager sender, CurrentSessionChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() => SelectSession());

    private void OnSessionsChanged(SessionManager sender, SessionsChangedEventArgs args) =>
        _dispatcher.InvokeAsync(WatchSessions);

    private void OnAnyPlaybackChanged(Session sender, PlaybackInfoChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() => SelectSession(sender));

    private void WatchSessions()
    {
        UnwatchSessions();
        if (_manager is null)
        {
            return;
        }

        _watched = _manager.GetSessions();
        foreach (var session in _watched)
        {
            session.PlaybackInfoChanged += OnAnyPlaybackChanged;
        }

        SelectSession();
    }

    private void UnwatchSessions()
    {
        foreach (var session in _watched)
        {
            session.PlaybackInfoChanged -= OnAnyPlaybackChanged;
        }

        _watched = [];
    }

    /// <summary>
    /// Windows' "current" session often stays on an idle app (e.g. a paused browser) while Telegram plays,
    /// so follow whatever is actually playing: the session that just started, then the one shown, then any other.
    /// </summary>
    private void SelectSession(Session? started = null)
    {
        if (_manager is null)
        {
            return;
        }

        var shown = _watched.FirstOrDefault(s => SameApp(s, _session));
        var current = _manager.GetCurrentSession();
        var next = IsPlaying(started) ? started
            : IsPlaying(shown) ? shown
            : _watched.FirstOrDefault(IsPlaying) ?? shown ?? _watched.FirstOrDefault(s => SameApp(s, current));

        // Compare the object, not the app: Telegram closes its session on stop and opens a new one on replay,
        // and staying on the dead one means its events never arrive again.
        if (ReferenceEquals(next, _session))
        {
            return;
        }

        Attach(next);
    }

    private static readonly string[] Browsers = ["TheBrowserCompany.Arc", "Chrome", "MSEdge", "Firefox", "Brave", "Opera", "Vivaldi"];

    private static bool IsBrowser(string app) =>
        Browsers.Any(b => app.Contains(b, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Browser media: the YouTube logo (and the "watch in the island" button) for YouTube; the site's own artwork
    /// when it sends some (Spotify, SoundCloud… album covers); otherwise, when the browser sends just its own logo, the
    /// site's icon. Windows doesn't say which site plays, so read the address of the tab titled like the media.
    /// </summary>
    private async Task<System.Windows.Media.ImageSource?> SiteArtworkAsync(string title, IRandomAccessStreamReference? thumbnail)
    {
        var address = await Task.Run(() => BrowserVideo.FindTabAddress(title));
        var host = BrowserVideo.ParseHost(address);
        var musicSite = host is not null && IsMusicSite(host);
        if (!musicSite && BrowserVideo.ParseVideoId(address) is not null)
        {
            Activity.CanWatchInIsland = true;
            return ArtworkLoader.YouTubeLogo; // drawn here: it needs the UI thread
        }

        var artwork = await Task.Run(() => ArtworkLoader.LoadAsync(thumbnail));
        var cover = artwork is BitmapSource bitmap && !ArtworkPalette.HasTransparentCorners(bitmap) ? bitmap : null;

        // "Watch in the island" is for video only. Windows doesn't say whether media has video (Chrome calls even
        // YouTube "music"), so: known music sites, or a square cover from any site (album art; video sites send wide
        // thumbnails or nothing), count as music.
        var isMusic = musicSite || (cover is not null && Math.Abs(cover.PixelWidth - cover.PixelHeight) <= 2);
        Activity.CanWatchInIsland = host is not null && !isMusic;

        return cover ?? (host is not null ? await SiteIcon.LoadAsync(host) : null);
    }

    private static readonly string[] MusicSites =
        ["spotify.com", "soundcloud.com", "music.youtube.com", "deezer.com", "tidal.com", "music.apple.com", "bandcamp.com", "radiojavan.com", "music.amazon.com"];

    /// <summary>The host is one of <see cref="MusicSites"/> or a subdomain of one (open.spotify.com).</summary>
    private static bool IsMusicSite(string host) =>
        MusicSites.Any(site => host.Equals(site, StringComparison.OrdinalIgnoreCase) || host.EndsWith("." + site, StringComparison.OrdinalIgnoreCase));

    private static bool SameApp(Session? a, Session? b) =>
        a is not null && b is not null && a.SourceAppUserModelId == b.SourceAppUserModelId;

    private static bool IsPlaying(Session? session)
    {
        try
        {
            return session?.GetPlaybackInfo().PlaybackStatus == PlaybackStatus.Playing;
        }
        catch (COMException)
        {
            return false; // session closed under us
        }
    }

    private void OnMediaPropertiesChanged(Session sender, MediaPropertiesChangedEventArgs args) =>
        _dispatcher.InvokeAsync(() => _ = RefreshPropertiesAsync());

    private void OnPlaybackInfoChanged(Session sender, PlaybackInfoChangedEventArgs args) =>
        _dispatcher.InvokeAsync(RefreshPlayback);

    private void OnTimelinePropertiesChanged(Session sender, TimelinePropertiesChangedEventArgs args) =>
        _dispatcher.InvokeAsync(RefreshTimeline);

    private void Attach(Session? session)
    {
        Detach();
        _session = session;

        if (session is null)
        {
            _status = PlaybackStatus.Closed;
            UpdateVisibility();
            return;
        }

        Activity.Volume = Activity.IsExpanded ? GetVolume() : null; // another app now: its own slider, or none
        session.MediaPropertiesChanged += OnMediaPropertiesChanged;
        session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        session.TimelinePropertiesChanged += OnTimelinePropertiesChanged;

        RefreshPlayback();
        RefreshTimeline();
        _ = RefreshPropertiesAsync();
    }

    private void Detach()
    {
        if (_session is null)
        {
            return;
        }

        _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
        _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        _session.TimelinePropertiesChanged -= OnTimelinePropertiesChanged;
        _session = null;
    }

    private async Task RefreshPropertiesAsync()
    {
        if (_session is not { } session)
        {
            return;
        }

        // Track changes can arrive in bursts; only the latest request may write state.
        var version = ++_propertiesVersion;
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync();
            if (version != _propertiesVersion)
            {
                return;
            }

            var app = session.SourceAppUserModelId;
            Activity.CanWatchInIsland = false; // SiteArtworkAsync turns it on for YouTube
            if (string.IsNullOrWhiteSpace(properties.Title))
            {
                // Telegram opens a new session on every replay but only sends metadata the first time a track plays,
                // so reuse what this app last sent. shortcut: a different untagged track from the same app shows the
                // previous title; fine while Telegram is the only app that does this.
                var known = _lastKnown is { } last && last.App == app ? last : default;
                Activity.Title = known.Title ?? System.IO.Path.GetFileNameWithoutExtension(app.Split('!')[^1]);
                Activity.Artist = known.Artist ?? string.Empty;
                Activity.Artwork = known.Artwork;
                UpdateVisibility();
                return;
            }

            Activity.Title = properties.Title;
            Activity.Artist = properties.Artist ?? string.Empty;
            UpdateVisibility();

            // Decode off the UI thread; the bitmap comes back frozen.
            var artwork = IsBrowser(app)
                ? await SiteArtworkAsync(properties.Title, properties.Thumbnail)
                : await Task.Run(() => ArtworkLoader.LoadAsync(properties.Thumbnail));
            if (version == _propertiesVersion)
            {
                Activity.Artwork = artwork;
                _lastKnown = (app, Activity.Title, Activity.Artist, artwork);
            }
        }
        catch (Exception ex) when (ex is COMException or InvalidOperationException)
        {
            Debug.WriteLine($"Media properties unavailable: {ex.Message}");
        }
    }

    private void RefreshPlayback()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var info = _session.GetPlaybackInfo();
            _status = info.PlaybackStatus;
            Activity.CanSeek = info.Controls.IsPlaybackPositionEnabled;
            Activity.CanSkipNext = info.Controls.IsNextEnabled;
            Activity.CanSkipPrevious = info.Controls.IsPreviousEnabled;
            Activity.CanShuffle = info.Controls.IsShuffleEnabled;
            Activity.IsShuffleOn = info.IsShuffleActive == true;
            Activity.CanRepeat = info.Controls.IsRepeatEnabled;
            Activity.RepeatMode = info.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;
            Activity.IsPlaying = _status == PlaybackStatus.Playing;
            UpdateVisibility();
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Playback info unavailable: {ex.Message}");
        }
    }

    private void RefreshTimeline()
    {
        if (_session is null)
        {
            return;
        }

        try
        {
            var timeline = _session.GetTimelineProperties();
            _timelineStart = timeline.StartTime;
            Activity.SyncTimeline(
                timeline.Position - timeline.StartTime,
                timeline.EndTime - timeline.StartTime,
                timeline.LastUpdatedTime);
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Timeline unavailable: {ex.Message}");
        }
    }

    private bool IsVisible => Activity.Title.Length > 0 && _status is PlaybackStatus.Playing or PlaybackStatus.Paused;

    private void UpdateVisibility()
    {
        if (IsVisible)
        {
            _hideDelay.Stop();
            _coordinator.Show(Activity);
        }
        else if (!_hideDelay.IsEnabled)
        {
            // A skip passes through Changing/Stopped (Telegram even swaps sessions); hiding at once would collapse the island.
            _hideDelay.Start();
        }
    }

    private void OnHideDelayElapsed(object? sender, EventArgs e)
    {
        _hideDelay.Stop();
        if (!IsVisible)
        {
            _coordinator.Hide(Activity);
        }
    }
}
