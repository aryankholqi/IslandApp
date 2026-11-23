using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using IslandApp.Core;

namespace IslandApp.Activities;

/// <summary>
/// An IDM download or an Explorer file copy in progress: a progress ring in the side bubble; widened, the speed and
/// time left. Each service owns its own instance.
/// </summary>
public sealed partial class DownloadActivity : IslandActivity
{
    [ObservableProperty]
    private double _fraction;

    [ObservableProperty]
    private string _speedText = string.Empty;

    [ObservableProperty]
    private string _timeLeftText = string.Empty;

    public override ActivityPriority Priority => ActivityPriority.Download;

    public override bool CanBeCompanion => true;

    public override bool BubbleOnly => true;

    public override ActivityTone Tone => ActivityTone.Accent;

    public override Size CompactSize { get; } = new(230, 36);

    public void Update(double fraction, string speed, string timeLeft)
    {
        Fraction = Math.Clamp(fraction, 0, 1);
        SpeedText = speed;
        TimeLeftText = timeLeft;
    }
}
