using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using IslandApp.Core;

namespace IslandApp.Activities;

/// <summary>A brief "icon · title · detail" flash, e.g. the input language switching or the VPN dropping.</summary>
public sealed partial class StatusActivity : IslandActivity
{
    [ObservableProperty]
    private string _glyph = string.Empty;

    [ObservableProperty]
    private string _title = string.Empty;

    [ObservableProperty]
    private string _detail = string.Empty;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Tone))]
    private ActivityTone _statusTone;

    public override ActivityPriority Priority => ActivityPriority.High;

    public override ActivityTone Tone => StatusTone;

    public override Size CompactSize { get; } = new(260, 36);

    public void Update(string glyph, string title, string detail, ActivityTone tone)
    {
        Glyph = glyph;
        Title = title;
        Detail = detail;
        StatusTone = tone;
    }
}
