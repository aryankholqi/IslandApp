using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using IslandApp.Activities;
using IslandApp.Controls;
using IslandApp.Core;
using IslandApp.Interop;
using IslandApp.Services;
using IslandApp.ViewModels;
using IslandApp.Views;

namespace IslandApp;

/// <summary>Composition root: wires services, activities and the island window.</summary>
public partial class App : Application
{
    private const string SingleInstanceMutexName = "Island.SingleInstance.6F1B3C2E";

    // The "Island Settings" shortcut runs `Island.exe --settings`; if the island is already running it signals this event instead.
    private const string SettingsArgument = "--settings";
    private const string ShowSettingsEventName = "Island.ShowSettings.6F1B3C2E";

    private static readonly Color DefaultAccent = Color.FromRgb(0xF4, 0x29, 0x02);

    private readonly List<IDisposable> _disposables = [];
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    private EventWaitHandle? _showSettingsSignal;
    private RegisteredWaitHandle? _showSettingsWait;
    private AppSettings? _settings;
    private SettingsWindow? _settingsWindow;
    private ClaudeUsageService? _claude;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // Claude Code runs us as its status line: answer and exit before any window, mutex or service starts.
        if (e.Args.Contains(ClaudeCode.StatusLineArgument, StringComparer.Ordinal))
        {
            ClaudeCode.RunStatusLineOverStdio();
            Shutdown();
            return;
        }

        var wantsSettings = e.Args.Contains(SettingsArgument, StringComparer.OrdinalIgnoreCase);
        _showSettingsSignal = new EventWaitHandle(false, EventResetMode.AutoReset, ShowSettingsEventName);
        _singleInstanceMutex = new Mutex(initiallyOwned: true, SingleInstanceMutexName, out _ownsMutex);
        if (!_ownsMutex)
        {
            if (wantsSettings)
            {
                // The running instance didn't get the click, so let it bring its window to the front.
                NativeMethods.AllowAnyForeground();
                _showSettingsSignal.Set();
            }

            Shutdown();
            return;
        }

        var settings = _settings = AppSettings.Load();
        var coordinator = new ActivityCoordinator
        {
            IsAllowed = activity => activity switch
            {
                MediaActivity => settings.ShowMedia,
                VolumeActivity => settings.ShowVolume,
                MessageActivity => settings.ShowNotifications,
                ChargingActivity => settings.ShowCharging,
                PrivacyActivity or CaptureActivity => settings.ShowPrivacy,
                BatteryActivity => settings.ShowBattery,
                _ => true,
            },
        };
        var media = new MediaSessionService(coordinator, Dispatcher, new VideoActivity(coordinator));
        var volume = new VolumeService(coordinator, Dispatcher);
        var power = new PowerService(coordinator, Dispatcher);
        var notifications = new NotificationService(coordinator);
        var timer = new TimerActivity(coordinator);
        var claude = _claude = new ClaudeUsageService(coordinator, Dispatcher);
        var foreground = new ForegroundAppService();
        var privacy = new PrivacyService(coordinator, Dispatcher);
        var fullScreen = new FullScreenService();
        var downloads = new DownloadService(coordinator);
        var fileCopy = new FileCopyService(coordinator);
        var keyboard = new KeyboardService(coordinator);
        var network = new NetworkService(coordinator);
        var bluetooth = new BluetoothService(coordinator, Dispatcher);
        var shelf = new ShelfActivity(coordinator);
        _disposables.AddRange([media, volume, power, notifications, timer, claude, foreground, privacy, fullScreen, downloads, fileCopy, keyboard, network, bluetooth]);

        // Claude usage rings while an IDE is focused (they move to the side bubble when media plays).
        var rings = new ClaudeRingsActivity(claude.Activity);
        void UpdateRings()
        {
            if (settings.ClaudeRingsInIde && foreground.IsIdeFocused && claude.Activity.HasData)
            {
                coordinator.Show(rings);
            }
            else
            {
                coordinator.Hide(rings);
            }
        }

        foreground.IdeFocusChanged += (_, _) => UpdateRings();
        claude.Activity.PropertyChanged += (_, _) => UpdateRings();

        var window = new IslandWindow(new IslandViewModel(coordinator, timer, shelf, ShowSettings, claude.Show));

        // Every setting is pushed from here, at startup and on each change in Island Settings.
        void Apply()
        {
            ApplyAccent(settings.Accent);
            volume.DisplayDuration = Seconds(settings.VolumeSeconds);
            power.DisplayDuration = Seconds(settings.ChargingSeconds);
            power.LowPercent = Math.Clamp(settings.BatteryLowPercent, 5, 50);
            power.ShowWhileCharging = settings.BatteryWhileCharging;
            power.RefreshBattery();
            notifications.DisplayDuration = Seconds(settings.NotificationSeconds);
            notifications.IncludeAllApps = settings.NotificationsFromAllApps;
            media.Activity.UseArtworkColors = settings.ArtworkGlow; // also re-reads the accent
            Waveform.LevelSource = settings.SyncWaveform ? () => volume.PeakLevel : null;
            privacy.AnnounceStarts = settings.PrivacyFlash;
            claude.MilestoneStep = Math.Clamp(settings.ClaudeStep, 0, 100);
            foreground.SetIdeProcesses(settings.IdeProcessNames());
            UpdateRings();
            coordinator.Refresh();
            window.ApplySettings(settings);
        }

        Apply();
        settings.PropertyChanged += (_, _) => Apply();
        MainWindow = window;
        window.Show();

        _showSettingsWait = ThreadPool.RegisterWaitForSingleObject(
            _showSettingsSignal, (_, _) => Dispatcher.InvokeAsync(ShowSettings), null, Timeout.Infinite, executeOnlyOnce: false);
        if (wantsSettings)
        {
            ShowSettings();
        }

        // Out of the way while a game (or any full-screen app) runs. The island is hidden then, so its pollers
        // stop too and the game gets the CPU.
        fullScreen.Changed += (_, _) =>
        {
            var gaming = fullScreen.IsActive;
            window.SetHiddenForFullScreen(gaming);
            downloads.SetPaused(gaming);
            fileCopy.SetPaused(gaming);
            keyboard.SetPaused(gaming);
            network.SetPaused(gaming);
            notifications.SetPaused(gaming);
        };

        volume.Start();
        claude.Start();
        foreground.Start();
        privacy.Start();
        downloads.Start();
        fileCopy.Start();
        keyboard.Start();
        network.Start();
        bluetooth.Start();
        UpdateRings();
        power.Start();

        try
        {
            await media.StartAsync();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Media session monitoring unavailable: {ex.Message}");
        }

        try
        {
            await notifications.StartAsync();
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Notification monitoring unavailable: {ex.Message}");
        }

        fullScreen.Start(); // last, so a game already running pauses services that have started
    }

    private static TimeSpan Seconds(double value) =>
        TimeSpan.FromSeconds(double.IsFinite(value) ? Math.Clamp(value, 0.5, 60) : 2);

    /// <summary>Replaces the accent resources app-wide; everything uses them via DynamicResource, so it repaints live.</summary>
    private static void ApplyAccent(string? hex)
    {
        var color = DefaultAccent;
        try
        {
            if (!string.IsNullOrWhiteSpace(hex))
            {
                color = (Color)ColorConverter.ConvertFromString(hex);
            }
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            // Hand-edited garbage: keep the default.
        }

        static byte Lighter(byte channel) => (byte)(channel + ((255 - channel) * 0.4));
        var resources = Current.Resources;
        resources["AccentColor"] = color;
        resources["AccentBrush"] = Frozen(new SolidColorBrush(color));
        resources["AccentSoftBrush"] = Frozen(new SolidColorBrush(Color.FromArgb(0x2E, color.R, color.G, color.B)));
        resources["ArtworkFallbackBrush"] = Frozen(new LinearGradientBrush(color, Color.FromRgb(Lighter(color.R), Lighter(color.G), Lighter(color.B)), 45));
    }

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }

    private void ShowSettings()
    {
        if (_settingsWindow is null)
        {
            _settingsWindow = new SettingsWindow(_settings!, _claude!.Activity);
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
        }

        if (_settingsWindow.WindowState == WindowState.Minimized)
        {
            _settingsWindow.WindowState = WindowState.Normal;
        }

        _settingsWindow.Activate();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _showSettingsWait?.Unregister(null);
        _showSettingsSignal?.Dispose();
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        if (_ownsMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
