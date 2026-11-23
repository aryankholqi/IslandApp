using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Text.RegularExpressions;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using Microsoft.Win32;
using Windows.Networking.Connectivity;

namespace IslandApp.Services;

/// <summary>
/// Flashes the island when the VPN connects or drops, and when the internet goes or comes back. Polled every 2 s:
/// a proxy-mode VPN (v2rayN and the like) only flips a registry value, which raises no network event.
/// </summary>
public sealed partial class NetworkService : IDisposable
{
    private const string InternetSettings = @"Software\Microsoft\Windows\CurrentVersion\Internet Settings";

    private readonly ActivityCoordinator _coordinator;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(2) };
    private string? _vpn;
    private bool _online;

    public NetworkService(ActivityCoordinator coordinator)
    {
        _coordinator = coordinator;
        _poll.Tick += (_, _) => Update();
    }

    public TimeSpan DisplayDuration { get; set; } = TimeSpan.FromSeconds(2.5);

    public StatusActivity Activity { get; } = new();

    public void Start()
    {
        _vpn = ActiveVpn();
        _online = IsOnline();
        _poll.Start();
    }

    public void Dispose() => _poll.Stop();

    /// <summary>Resuming re-reads the state, so a change made during a game doesn't flash afterwards.</summary>
    public void SetPaused(bool paused)
    {
        if (paused)
        {
            _poll.Stop();
        }
        else
        {
            Start();
        }
    }

    private void Update()
    {
        var vpn = ActiveVpn();
        var online = IsOnline();
        var vpnChanged = vpn != _vpn;
        var onlineChanged = online != _online;
        var hadVpn = _vpn;
        (_vpn, _online) = (vpn, online);

        // Losing the internet matters most; a VPN change that comes with it is usually the cause, not news.
        if (onlineChanged && !online)
        {
            Show("", "No internet", hadVpn is not null && vpn is null ? "VPN disconnected" : string.Empty, ActivityTone.Accent);
        }
        else if (vpnChanged && vpn is not null)
        {
            Show("", "VPN connected", vpn, ActivityTone.Positive);
        }
        else if (vpnChanged)
        {
            Show("", "VPN disconnected", hadVpn!, ActivityTone.Accent);
        }
        else if (onlineChanged)
        {
            Show("", "Back online", string.Empty, ActivityTone.Positive);
        }
    }

    private void Show(string glyph, string title, string detail, ActivityTone tone)
    {
        Activity.Update(glyph, title, detail, tone);
        _coordinator.Flash(Activity, DisplayDuration);
    }

    /// <summary>Whether Windows says this PC reaches the internet (the taskbar's network icon uses the same check).</summary>
    private static bool IsOnline()
    {
        try
        {
            return NetworkInformation.GetInternetConnectionProfile()?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Connectivity unavailable: {ex.Message}");
            return true; // unknown: never claim "No internet"
        }
    }

    /// <summary>The VPN in use (adapter name, or "System proxy"), or null.</summary>
    private static string? ActiveVpn()
    {
        try
        {
            foreach (var adapter in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (adapter.OperationalStatus == OperationalStatus.Up && IsVpnAdapter(adapter.NetworkInterfaceType, adapter.Description))
                {
                    return adapter.Name;
                }
            }
        }
        catch (NetworkInformationException ex)
        {
            Debug.WriteLine($"Adapters unavailable: {ex.Message}");
        }

        using var settings = Registry.CurrentUser.OpenSubKey(InternetSettings);
        return settings?.GetValue("ProxyEnable") is 1 ? "System proxy" : null;
    }

    /// <summary>Built-in VPNs show as PPP; OpenVPN, WireGuard and TUN-mode clients by their driver's description.</summary>
    internal static bool IsVpnAdapter(NetworkInterfaceType type, string description) =>
        !NotVpnRegex().IsMatch(description)
        && (type is NetworkInterfaceType.Ppp || VpnRegex().IsMatch(description));

    [GeneratedRegex(@"(?i)\bvpn\b|tap-|\btap\b|wireguard|wintun|\btun\b|tunnel|openvpn|warp")]
    private static partial Regex VpnRegex();

    // Windows' own IPv6 transition tunnels and Hyper-V adapters are always up and aren't VPNs.
    [GeneratedRegex(@"(?i)teredo|isatap|6to4|ip-https|loopback|hyper-v|wan miniport")]
    private static partial Regex NotVpnRegex();
}
