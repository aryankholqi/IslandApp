using System.Globalization;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using IslandApp.Core;

namespace IslandApp.Activities;

/// <summary>Battery level as a ring, shown while charging or when low (Island Settings). Lives in the bubble when the island is busy.</summary>
public sealed partial class BatteryActivity : IslandActivity
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Fraction), nameof(PercentText), nameof(PercentNumber))]
    private int _percent;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(Tone))]
    private bool _isCharging;

    public override ActivityPriority Priority => ActivityPriority.Battery;

    public override bool CanBeCompanion => true;

    public override ActivityTone Tone => IsCharging ? ActivityTone.Positive : ActivityTone.Accent;

    public override Size CompactSize { get; } = new(230, 36);

    public double Fraction => Percent / 100.0;

    public string PercentText => PercentNumber + "%";

    public string PercentNumber => Percent.ToString(CultureInfo.InvariantCulture);

    public string StatusText => IsCharging ? "Charging" : "Low battery";

    public void Update(int percent, bool isCharging)
    {
        Percent = Math.Clamp(percent, 0, 100);
        IsCharging = isCharging;
    }
}
