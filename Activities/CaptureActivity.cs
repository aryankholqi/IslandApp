using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using IslandApp.Core;

namespace IslandApp.Activities;

/// <summary>What is being captured, most important first: screen sharing wins over camera, camera over microphone.</summary>
public enum CaptureKind
{
    Screen,
    Camera,
    Microphone,
}

/// <summary>
/// Screen, camera and/or microphone in use: an icon for each, plus the app and how long for the most important one.
/// Persistent while any app uses them: on the island when it's free, in the side bubble when media has the island.
/// </summary>
public sealed partial class CaptureActivity : IslandActivity, IDisposable
{
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private DateTimeOffset _since;

    /// <summary>The most important kind in use; the app name and time are its.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tone))]
    private CaptureKind _kind;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BubbleWidth))]
    private bool _isScreen;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BubbleWidth))]
    private bool _isCamera;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BubbleWidth))]
    private bool _isMicrophone;

    [ObservableProperty]
    private string _appName = string.Empty;

    [ObservableProperty]
    private string _durationText = "0:00";

    public CaptureActivity() => _ticker.Tick += (_, _) => Tick();

    public override ActivityPriority Priority => ActivityPriority.Capture;

    public override bool CanBeCompanion => true;

    public override bool BubbleOnly => true;

    public override ActivityTone Tone => Kind switch
    {
        CaptureKind.Camera => ActivityTone.Positive,
        CaptureKind.Microphone => ActivityTone.Accent,
        _ => ActivityTone.Neutral,
    };

    public override Size CompactSize { get; } = new(260, 36);

    /// <summary>
    /// Every icon in use fits the closed bubble: 10 px in, 23 px per icon (16 glyph + 7 gap), 10 px out less the last
    /// gap. One icon gives the usual 36 px circle. Matches the CaptureActivity template in BubbleTemplates.xaml.
    /// </summary>
    public override double BubbleWidth => 13 + (23 * Math.Max(1, (IsScreen ? 1 : 0) + (IsCamera ? 1 : 0) + (IsMicrophone ? 1 : 0)));

    /// <summary>
    /// Starts or updates the display. <paramref name="kind"/> is the most important of <paramref name="inUse"/>;
    /// <paramref name="since"/> is when its app started using it.
    /// </summary>
    public void Update(CaptureKind kind, IReadOnlySet<CaptureKind> inUse, string appName, DateTimeOffset since)
    {
        Kind = kind;
        IsScreen = inUse.Contains(CaptureKind.Screen);
        IsCamera = inUse.Contains(CaptureKind.Camera);
        IsMicrophone = inUse.Contains(CaptureKind.Microphone);
        AppName = appName;
        _since = since;
        Tick();
        _ticker.Start();
    }

    public void Stop() => _ticker.Stop();

    public void Dispose() => _ticker.Stop();

    private void Tick() => DurationText = TimeFormat.Short(DateTimeOffset.Now - _since);
}
