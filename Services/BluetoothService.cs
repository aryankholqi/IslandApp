using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace IslandApp.Services;

/// <summary>
/// Flashes the island when a paired Bluetooth device (headphones, controller, mouse…) connects or disconnects, with
/// its battery level when Windows knows it (the same figure Settings › Bluetooth shows).
/// shortcut: BLE devices that report battery only over GATT show no percentage; reading GATT would need a connection per device.
/// </summary>
public sealed class BluetoothService : IDisposable
{
    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private const string ContainerKey = "System.Devices.Aep.ContainerId";
    private const string CategoryKey = "System.Devices.Aep.Category";
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // DEVPKEY_Bluetooth_Battery

    private readonly ActivityCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private readonly Dictionary<string, Device> _devices = [];
    private DeviceWatcher? _watcher;
    private bool _enumerated;

    public BluetoothService(ActivityCoordinator coordinator, Dispatcher dispatcher)
    {
        _coordinator = coordinator;
        _dispatcher = dispatcher;
    }

    public TimeSpan DisplayDuration { get; set; } = TimeSpan.FromSeconds(3);

    public StatusActivity Activity { get; } = new();

    public void Start()
    {
        try
        {
            var selector = $"({BluetoothDevice.GetDeviceSelectorFromPairingState(true)}) OR ({BluetoothLEDevice.GetDeviceSelectorFromPairingState(true)})";
            _watcher = DeviceInformation.CreateWatcher(selector, [IsConnectedKey, ContainerKey, CategoryKey], DeviceInformationKind.AssociationEndpoint);
            _watcher.Added += (_, info) => _dispatcher.InvokeAsync(() => OnAdded(info));
            _watcher.Updated += (_, update) => _dispatcher.InvokeAsync(() => OnUpdated(update));
            _watcher.Removed += (_, update) => _dispatcher.InvokeAsync(() => _devices.Remove(update.Id));
            _watcher.EnumerationCompleted += (_, _) => _dispatcher.InvokeAsync(() => _enumerated = true);
            _watcher.Start();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Bluetooth unavailable: {ex.Message}"); // no adapter, or radio access denied
        }
    }

    public void Dispose()
    {
        if (_watcher is { Status: DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted })
        {
            _watcher.Stop();
        }
    }

    private void OnAdded(DeviceInformation info)
    {
        var device = new Device(info.Name, Get<Guid?>(info.Properties, ContainerKey), Get<string[]>(info.Properties, CategoryKey) ?? []);
        _devices[info.Id] = device;
        device.IsConnected = Get<bool?>(info.Properties, IsConnectedKey) == true;
        if (_enumerated && device.IsConnected)
        {
            _ = AnnounceAsync(device); // paired just now and already connected
        }
    }

    private void OnUpdated(DeviceInformationUpdate update)
    {
        if (!_devices.TryGetValue(update.Id, out var device) || Get<bool?>(update.Properties, IsConnectedKey) is not { } connected
            || connected == device.IsConnected)
        {
            return; // not a connection change (Windows also reports signal strength and the like)
        }

        device.IsConnected = connected;
        _ = AnnounceAsync(device);
    }

    private async Task AnnounceAsync(Device device)
    {
        var glyph = Glyph(device.Categories);
        if (!device.IsConnected)
        {
            Show(glyph, device.Name, "Disconnected", ActivityTone.Neutral);
            return;
        }

        Show(glyph, device.Name, "Connected", ActivityTone.Positive);

        // Headsets report battery a moment after connecting; update the flash if it's still up.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (await BatteryAsync(device) is { } percent)
            {
                if (device.IsConnected && ReferenceEquals(_coordinator.Current, Activity) && Activity.Title == device.Name)
                {
                    Activity.Update(glyph, device.Name, $"{percent}%", ActivityTone.Positive);
                }

                return;
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private void Show(string glyph, string name, string detail, ActivityTone tone)
    {
        Activity.Update(glyph, name, detail, tone);
        _coordinator.Flash(Activity, DisplayDuration);
    }

    /// <summary>The battery figure lives on the device's PnP nodes (same container), not on its Bluetooth endpoint.</summary>
    private static async Task<int?> BatteryAsync(Device device)
    {
        if (device.Container is not { } container)
        {
            return null;
        }

        try
        {
            var nodes = await DeviceInformation.FindAllAsync($"System.Devices.ContainerId:=\"{{{container}}}\"", [BatteryKey], DeviceInformationKind.Device);
            return nodes
                .Select(n => n.Properties.TryGetValue(BatteryKey, out var value) && value is not null ? Convert.ToInt32(value) : (int?)null)
                .FirstOrDefault(percent => percent is not null);
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Bluetooth battery unavailable: {ex.Message}");
            return null;
        }
    }

    // Segoe Fluent Icons: headphones, game controller, keyboard, mouse, phone, else the Bluetooth mark.
    private static string Glyph(string[] categories) => string.Join(";", categories) switch
    {
        var c when c.Contains("Audio", StringComparison.OrdinalIgnoreCase) => "",
        var c when c.Contains("Gaming", StringComparison.OrdinalIgnoreCase) => "",
        var c when c.Contains("Keyboard", StringComparison.OrdinalIgnoreCase) => "",
        var c when c.Contains("Mouse", StringComparison.OrdinalIgnoreCase) => "",
        var c when c.Contains("Phone", StringComparison.OrdinalIgnoreCase) => "",
        _ => "",
    };

    private static T? Get<T>(IReadOnlyDictionary<string, object> properties, string key) =>
        properties.TryGetValue(key, out var value) && value is T typed ? typed : default;

    private sealed class Device(string name, Guid? container, string[] categories)
    {
        public string Name { get; } = name;

        public Guid? Container { get; } = container;

        public string[] Categories { get; } = categories;

        public bool IsConnected { get; set; }
    }
}
