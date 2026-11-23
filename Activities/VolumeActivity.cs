using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using IslandApp.Core;

namespace IslandApp.Activities;

public sealed partial class VolumeActivity : IslandActivity
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Percent))]
    private double _level;

    [ObservableProperty]
    private bool _isMuted;

    public override ActivityPriority Priority => ActivityPriority.High;

    public override Size CompactSize { get; } = new(300, 44);

    public int Percent => (int)Math.Round(Level * 100);

    public void Update(double level, bool muted)
    {
        Level = Math.Clamp(level, 0, 1);
        IsMuted = muted;
    }
}
