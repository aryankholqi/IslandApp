using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using IslandApp.Interop;

namespace IslandApp.Services;

/// <summary>
/// Listens to the default output device's master volume (Core Audio) and flashes the island on change.
/// Follows the default device when the user switches outputs.
/// </summary>
public sealed class VolumeService : IDisposable
{

    private readonly ActivityCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private readonly VolumeCallback _volumeCallback;
    private readonly DeviceCallback _deviceCallback;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private IAudioMeterInformation? _meter;

    public VolumeService(ActivityCoordinator coordinator, Dispatcher dispatcher)
    {
        _coordinator = coordinator;
        _dispatcher = dispatcher;
        _volumeCallback = new VolumeCallback(OnVolumeNotified);
        _deviceCallback = new DeviceCallback(() => _dispatcher.InvokeAsync(BindDefaultEndpoint));
    }

    /// <summary>How long the island shows this activity (Island Settings).</summary>
    public TimeSpan DisplayDuration { get; set; } = TimeSpan.FromSeconds(1.8);

    public VolumeActivity Activity { get; } = new();

    /// <summary>Current output peak (0..1) of the default device; 0 when unavailable. UI thread only.</summary>
    public float PeakLevel => _meter?.GetPeakValue(out var peak) == 0 ? peak : 0;

    public void Start()
    {
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            Marshal.ThrowExceptionForHR(_enumerator.RegisterEndpointNotificationCallback(_deviceCallback));
            BindDefaultEndpoint();
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Volume monitoring unavailable: {ex.Message}");
        }
    }

    public void Dispose()
    {
        ReleaseEndpoint();

        if (_enumerator is not null)
        {
            _enumerator.UnregisterEndpointNotificationCallback(_deviceCallback);
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    // Called on a Core Audio worker thread.
    private void OnVolumeNotified(float level, bool muted) =>
        _dispatcher.InvokeAsync(() =>
        {
            Activity.Update(level, muted);
            _coordinator.Flash(Activity, DisplayDuration);
        });

    private void BindDefaultEndpoint()
    {
        ReleaseEndpoint();

        if (_enumerator is null
            || _enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out var device) != 0
            || device is null)
        {
            return;
        }

        try
        {
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var instance) != 0)
            {
                return;
            }

            _endpoint = (IAudioEndpointVolume)instance;
            Marshal.ThrowExceptionForHR(_endpoint.RegisterControlChangeNotify(_volumeCallback));

            iid = typeof(IAudioMeterInformation).GUID;
            if (device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var meter) == 0)
            {
                _meter = (IAudioMeterInformation)meter;
            }
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"Could not bind audio endpoint: {ex.Message}");
            ReleaseEndpoint();
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void ReleaseEndpoint()
    {
        if (_meter is not null)
        {
            Marshal.ReleaseComObject(_meter);
            _meter = null;
        }

        if (_endpoint is null)
        {
            return;
        }

        _endpoint.UnregisterControlChangeNotify(_volumeCallback);
        Marshal.ReleaseComObject(_endpoint);
        _endpoint = null;
    }

    private sealed class VolumeCallback(Action<float, bool> onChanged) : IAudioEndpointVolumeCallback
    {
        public int OnNotify(IntPtr notifyData)
        {
            var (level, muted) = CoreAudio.ReadVolumeNotification(notifyData);
            onChanged(level, muted);
            return 0;
        }
    }

    private sealed class DeviceCallback(Action onDefaultRenderDeviceChanged) : IMMNotificationClient
    {
        public int OnDefaultDeviceChanged(EDataFlow flow, ERole role, string? defaultDeviceId)
        {
            if (flow == EDataFlow.Render && role == ERole.Multimedia)
            {
                onDefaultRenderDeviceChanged();
            }

            return 0;
        }

        public int OnDeviceStateChanged(string deviceId, uint newState) => 0;

        public int OnDeviceAdded(string deviceId) => 0;

        public int OnDeviceRemoved(string deviceId) => 0;

        public int OnPropertyValueChanged(string deviceId, PropertyKey key) => 0;
    }
}
