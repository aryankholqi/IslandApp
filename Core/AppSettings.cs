using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using CommunityToolkit.Mvvm.ComponentModel;
using IslandApp.Services;
using Microsoft.Win32;

namespace IslandApp.Core;

/// <summary>User preferences. Saved to %AppData%\Island\settings.json on every change.</summary>
public sealed partial class AppSettings : ObservableObject
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Island";

    private static readonly string FilePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Island", "settings.json");

    [ObservableProperty]
    private bool _showMedia = true;

    [ObservableProperty]
    private bool _showVolume = true;

    [ObservableProperty]
    private bool _showNotifications = true;

    [ObservableProperty]
    private bool _showCharging = true;

    [ObservableProperty]
    private bool _showPrivacy = true;

    // Per-feature options. Seconds/percent values are clamped where they're applied (the file is hand-editable).
    [ObservableProperty]
    private double _volumeSeconds = 1.8;

    [ObservableProperty]
    private double _chargingSeconds = 2.5;

    [ObservableProperty]
    private double _notificationSeconds = 5;

    [ObservableProperty]
    private bool _notificationsFromAllApps;

    [ObservableProperty]
    private bool _artworkGlow = true;

    [ObservableProperty]
    private bool _syncWaveform = true;

    [ObservableProperty]
    private bool _privacyFlash = true;

    [ObservableProperty]
    private bool _showBattery = true;

    [ObservableProperty]
    private int _batteryLowPercent = 20;

    [ObservableProperty]
    private bool _batteryWhileCharging = true;

    [ObservableProperty]
    private int _claudeStep = 10;

    [ObservableProperty]
    private bool _claudeRingsInIde = true;

    [ObservableProperty]
    private string _ideApps = DefaultIdeApps;

    // Appearance
    [ObservableProperty]
    private string _accent = "#F42902";

    [ObservableProperty]
    private double _scale = 1;

    [ObservableProperty]
    private double _glowOpacity = 0.45;

    /// <summary>Multiplies animation durations: above 1 is calmer, below 1 snappier.</summary>
    [ObservableProperty]
    private double _animationSpeed = 1;

    // Position: empty Monitor = the main display.
    [ObservableProperty]
    private string _monitor = string.Empty;

    [ObservableProperty]
    private double _topOffset;

    // Behaviour: 0 = never auto-collapse.
    [ObservableProperty]
    private double _collapseSeconds = 1.5;

    /// <summary>How long the side bubble stays widened after a click; 0 = until clicked again.</summary>
    [ObservableProperty]
    private double _bubbleCollapseSeconds = 1.5;

    [ObservableProperty]
    private string _timerPresets = "5, 25";

    public const string DefaultIdeApps =
        "Code, Code - Insiders, VSCodium, Cursor, Windsurf, Trae, Kiro, devenv, idea, idea64, pycharm64, webstorm64, " +
        "rider64, clion64, goland64, rustrover64, phpstorm64, rubymine64, datagrip64, dataspell64, aqua64, fleet, " +
        "studio64, zed, sublime_text, eclipse";

    /// <summary>Timer presets in minutes: 1..600, at most 6, in the order typed.</summary>
    public IReadOnlyList<int> TimerPresetMinutes() =>
        [.. (TimerPresets ?? string.Empty)
            .Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(text => int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var minutes) ? minutes : 0)
            .Where(minutes => minutes is >= 1 and <= 600)
            .Distinct()
            .Take(6)];

    /// <summary>IDE process names without ".exe".</summary>
    public IReadOnlyList<string> IdeProcessNames() =>
        [.. (IdeApps ?? string.Empty)
            .Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(name => name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name)
            .Where(name => name.Length > 0)];

    /// <summary>Lives in the registry Run key, not the JSON file, so the installer's uninstaller can clean it up.</summary>
    [JsonIgnore]
    public bool StartWithWindows
    {
        get
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is not null;
        }
        set
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (value)
            {
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
            }
            else
            {
                key.DeleteValue(RunValue, throwOnMissingValue: false);
            }

            OnPropertyChanged();
        }
    }

    /// <summary>Lives in Claude Code's own settings (its status line), so it can't drift from what Claude Code runs.</summary>
    [JsonIgnore]
    public bool ClaudeConnected
    {
        get
        {
            try
            {
                return ClaudeCode.IsConnected;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                return false;
            }
        }
        set
        {
            try
            {
                if (value)
                {
                    ClaudeCode.Connect();
                }
                else
                {
                    ClaudeCode.Disconnect();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                // The switch re-reads the real state below, so it flips back on failure.
                System.Diagnostics.Debug.WriteLine($"Could not update Claude Code settings: {ex.Message}");
            }

            OnPropertyChanged();
        }
    }

    public static AppSettings Load()
    {
        AppSettings? settings = null;
        try
        {
            settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Missing or corrupt file: fall back to defaults.
        }

        settings ??= new AppSettings();
        settings.PropertyChanged += (_, _) => settings.Save();
        return settings;
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            System.Diagnostics.Debug.WriteLine($"Could not save settings: {ex.Message}");
        }
    }
}
