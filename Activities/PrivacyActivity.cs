using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using IslandApp.Core;

namespace IslandApp.Activities;

/// <summary>
/// The brief "Microphone · app" flash when an app starts using the mic or camera.
/// While it stays in use, <see cref="CaptureActivity"/> shows it with a running time.
/// </summary>
public sealed partial class PrivacyActivity : IslandActivity
{
    /// <summary>Whether the announcement is about the camera (otherwise the microphone).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(KindText), nameof(Tone))]
    private bool _isCamera;

    [ObservableProperty]
    private string _appName = string.Empty;

    public override ActivityPriority Priority => ActivityPriority.High;

    public override ActivityTone Tone => IsCamera ? ActivityTone.Positive : ActivityTone.Accent;

    public override Size CompactSize { get; } = new(280, 36);

    public string KindText => IsCamera ? "Camera" : "Microphone";

    public void Announce(bool camera, string appName)
    {
        IsCamera = camera;
        AppName = appName;
    }
}
