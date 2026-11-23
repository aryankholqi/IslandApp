using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using IslandApp.Core;

namespace IslandApp.Activities;

public sealed partial class ChargingActivity : IslandActivity
{
    [ObservableProperty]
    private int _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Tone))]
    private bool _isCharging;

    public override ActivityPriority Priority => ActivityPriority.High;

    public override ActivityTone Tone => IsCharging ? ActivityTone.Positive : ActivityTone.Neutral;

    public override Size CompactSize { get; } = new(300, 40);

    public string StatusText => IsCharging ? "Charging" : "On battery";

    public void Update(int percent, bool isCharging)
    {
        Percent = Math.Clamp(percent, 0, 100);
        IsCharging = isCharging;
    }
}
