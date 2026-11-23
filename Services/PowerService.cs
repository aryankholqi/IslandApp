using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using Windows.System.Power;

namespace IslandApp.Services;

/// <summary>
/// Flashes the island when the charger is connected or disconnected, and keeps a battery ring up while charging
/// or when low. No-op on desktops without a battery.
/// </summary>
public sealed class PowerService : IDisposable
{
    private readonly ActivityCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private bool _isPluggedIn;
    private bool _isListening;

    public PowerService(ActivityCoordinator coordinator, Dispatcher dispatcher)
    {
        _coordinator = coordinator;
        _dispatcher = dispatcher;
    }

    /// <summary>How long the island shows this activity (Island Settings).</summary>
    public TimeSpan DisplayDuration { get; set; } = TimeSpan.FromSeconds(2.5);

    public ChargingActivity Activity { get; } = new();

    public BatteryActivity Battery { get; } = new();

    /// <summary>Show the battery ring at or below this percentage (Island Settings).</summary>
    public int LowPercent { get; set; } = 20;

    /// <summary>Also show the battery ring while charging, until full (Island Settings).</summary>
    public bool ShowWhileCharging { get; set; } = true;

    private static bool HasBattery => PowerManager.BatteryStatus != BatteryStatus.NotPresent;

    private static bool IsPluggedIn => PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;

    public void Start()
    {
        if (!HasBattery)
        {
            return;
        }

        _isPluggedIn = IsPluggedIn;
        PowerManager.PowerSupplyStatusChanged += OnPowerSupplyStatusChanged;
        PowerManager.RemainingChargePercentChanged += OnRemainingChargePercentChanged;
        _isListening = true;
        RefreshBattery();
    }

    /// <summary>Re-evaluates the battery ring; called on power events and when its settings change. UI thread only.</summary>
    public void RefreshBattery()
    {
        if (!_isListening)
        {
            return;
        }

        Battery.Update(PowerManager.RemainingChargePercent, IsPluggedIn);
        var show = Battery.IsCharging
            ? ShowWhileCharging && Battery.Percent < 100
            : Battery.Percent <= LowPercent;

        if (show)
        {
            _coordinator.Show(Battery);
        }
        else
        {
            _coordinator.Hide(Battery);
        }
    }

    public void Dispose()
    {
        if (_isListening)
        {
            PowerManager.PowerSupplyStatusChanged -= OnPowerSupplyStatusChanged;
            PowerManager.RemainingChargePercentChanged -= OnRemainingChargePercentChanged;
            _isListening = false;
        }
    }

    // Power events arrive on a background thread.
    private void OnRemainingChargePercentChanged(object? sender, object e) => _dispatcher.InvokeAsync(RefreshBattery);

    private void OnPowerSupplyStatusChanged(object? sender, object e) =>
        _dispatcher.InvokeAsync(() =>
        {
            RefreshBattery();

            var pluggedIn = IsPluggedIn;
            if (pluggedIn == _isPluggedIn)
            {
                return;
            }

            _isPluggedIn = pluggedIn;
            Activity.Update(PowerManager.RemainingChargePercent, pluggedIn);
            _coordinator.Flash(Activity, DisplayDuration);
        });
}
