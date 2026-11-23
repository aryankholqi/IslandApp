using System.Diagnostics;
using System.IO;
using System.Security;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using IslandApp.Interop;
using Microsoft.Win32;

namespace IslandApp.Services;

/// <summary>
/// Detects microphone, camera and screen-capture use from Windows' capability consent store, the same data behind the
/// taskbar's privacy indicator. An app is using a device while its entry has LastUsedTimeStart set and
/// LastUsedTimeStop = 0. Change notifications come from the registry itself; there is no polling.
/// </summary>
public sealed class PrivacyService : IDisposable
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string NonPackaged = "NonPackaged";
    // In priority order. Screen capture via Windows.Graphics.Capture lands under the graphicsCapture* entries;
    // shortcut: apps capturing through older APIs (BitBlt, desktop duplication) aren't recorded there, so they don't show.
    private static readonly (string Capability, CaptureKind Kind)[] Capabilities =
    [
        ("graphicsCaptureWithoutBorder", CaptureKind.Screen),
        ("graphicsCaptureProgrammatic", CaptureKind.Screen),
        ("webcam", CaptureKind.Camera),
        ("microphone", CaptureKind.Microphone),
    ];

    private readonly ActivityCoordinator _coordinator;
    private readonly Dispatcher _dispatcher;
    private readonly AutoResetEvent _changed = new(false);
    private readonly Dictionary<string, string> _displayNames = [];
    private RegistryKey? _store;
    private RegisteredWaitHandle? _wait;
    private HashSet<string> _inUse = [];

    public PrivacyService(ActivityCoordinator coordinator, Dispatcher dispatcher)
    {
        _coordinator = coordinator;
        _dispatcher = dispatcher;
    }

    /// <summary>How long the island shows this activity (Island Settings).</summary>
    public TimeSpan DisplayDuration { get; set; } = TimeSpan.FromSeconds(2);

    public PrivacyActivity Activity { get; } = new();

    /// <summary>Shown for as long as any app uses the mic, camera or screen capture.</summary>
    public CaptureActivity Capture { get; } = new();

    /// <summary>Flash the island with the app's name when it starts using the mic, camera or screen capture (Capture shows regardless).</summary>
    public bool AnnounceStarts { get; set; } = true;

    public void Start()
    {
        _store = Registry.CurrentUser.OpenSubKey(ConsentStore);
        if (_store is null)
        {
            return;
        }

        Arm();
        Scan(announce: false);
    }

    public void Dispose()
    {
        _wait?.Unregister(null);
        _wait = null;
        _store?.Dispose();
        _store = null;
        Capture.Dispose();
        _changed.Dispose();
    }

    /// <summary>A registry notification fires once; re-arm before rescanning so no change slips between them.</summary>
    private void Arm()
    {
        if (_store is null)
        {
            return;
        }

        _wait?.Unregister(null);
        if (NativeMethods.NotifyRegistryChange(_store.Handle, _changed.SafeWaitHandle) != 0)
        {
            Debug.WriteLine("Microphone/camera monitoring unavailable.");
            return;
        }

        _wait = ThreadPool.RegisterWaitForSingleObject(
            _changed, (_, _) => _dispatcher.InvokeAsync(OnStoreChanged), null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private void OnStoreChanged()
    {
        Arm();
        Scan(announce: true);
    }

    private void Scan(bool announce)
    {
        if (_store is null)
        {
            return;
        }

        try
        {
            var inUse = new HashSet<string>();
            var started = false;
            var kinds = new HashSet<CaptureKind>();
            (CaptureKind Kind, string App, long Since)? shown = null;
            foreach (var (capability, kind) in Capabilities)
            {
                foreach (var (app, since) in AppsInUse(capability))
                {
                    var id = capability + "|" + app;
                    inUse.Add(id);
                    kinds.Add(kind);
                    started |= !_inUse.Contains(id);

                    // Capabilities are in priority order, so a call sharing its screen shows as the screen;
                    // within a kind, the app that started most recently.
                    if (shown is null || (shown.Value.Kind == kind && since > shown.Value.Since))
                    {
                        shown = (kind, app, since);
                    }
                }
            }

            _inUse = inUse;
            if (shown is { } current)
            {
                Capture.Update(current.Kind, kinds, DisplayName(current.App), DateTimeOffset.FromFileTime(current.Since));
                _coordinator.Show(Capture);
            }
            else
            {
                Capture.Stop();
                _coordinator.Hide(Capture);
            }

            if (announce && AnnounceStarts && started)
            {
                // Show the new capture in the island first; when the flash ends it drops to the bubble if outranked.
                _coordinator.Flash(Capture, DisplayDuration);
            }
        }
        catch (Exception ex) when (ex is IOException or SecurityException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Consent store unreadable: {ex.Message}");
        }
    }

    /// <summary>Apps using the capability now, with when they started (FILETIME).</summary>
    private IEnumerable<(string App, long Since)> AppsInUse(string capability)
    {
        using var capabilityKey = _store!.OpenSubKey(capability);
        if (capabilityKey is null)
        {
            yield break;
        }

        foreach (var name in capabilityKey.GetSubKeyNames())
        {
            if (name != NonPackaged)
            {
                if (InUseSince(capabilityKey, name) is { } since)
                {
                    yield return (name, since);
                }

                continue;
            }

            using var desktopApps = capabilityKey.OpenSubKey(NonPackaged);
            foreach (var path in desktopApps?.GetSubKeyNames() ?? [])
            {
                if (InUseSince(desktopApps!, path) is { } since)
                {
                    yield return (path, since);
                }
            }
        }
    }

    /// <summary>When the app started using the device, or null if it isn't using it now.</summary>
    private static long? InUseSince(RegistryKey parent, string name)
    {
        using var app = parent.OpenSubKey(name);
        return app?.GetValue("LastUsedTimeStart") is long start && start > 0
            && (app.GetValue("LastUsedTimeStop") as long? ?? 0) == 0
            ? start
            : null;
    }

    /// <summary>"C:#Program Files#Telegram#Telegram.exe" → its file description; "MSTeams_8wekyb3d8bbwe" → "MSTeams".</summary>
    private string DisplayName(string app)
    {
        if (_displayNames.TryGetValue(app, out var cached))
        {
            return cached;
        }

        string name;
        if (app.Contains('#', StringComparison.Ordinal))
        {
            var path = app.Replace('#', '\\');
            string? description = null;
            try
            {
                description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            }
            catch (FileNotFoundException)
            {
                // Uninstalled since; fall back to the file name.
            }

            name = string.IsNullOrWhiteSpace(description) ? Path.GetFileNameWithoutExtension(path) : description.Trim();
        }
        else
        {
            var family = app.Split('_')[0];
            name = family[(family.LastIndexOf('.') + 1)..];
        }

        return _displayNames[app] = name;
    }
}
