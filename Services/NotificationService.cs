using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using IslandApp.Interop;
using Windows.ApplicationModel;
using Windows.UI.Notifications;
using Windows.UI.Notifications.Management;

namespace IslandApp.Services;

/// <summary>
/// Flashes the island for new Telegram toasts and toasts from Windows itself (Security, Update, devices…).
/// Polls the Action Center because the NotificationChanged event needs package identity (MSIX),
/// which this app doesn't have.
/// shortcut: 1.5 s polling, switch to the NotificationChanged event if the app is ever packaged.
/// </summary>
public sealed class NotificationService : IDisposable
{

    private readonly ActivityCoordinator _coordinator;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly UserNotificationListener _listener = UserNotificationListener.Current;
    private HashSet<uint>? _seen;
    private bool _isPolling;

    public NotificationService(ActivityCoordinator coordinator)
    {
        _coordinator = coordinator;
        Activity = new MessageActivity(Open);
        _poll.Tick += async (_, _) => await PollAsync();
    }

    /// <summary>How long the island shows this activity (Island Settings).</summary>
    public TimeSpan DisplayDuration { get; set; } = TimeSpan.FromSeconds(5);

    public MessageActivity Activity { get; }

    /// <summary>Show every app's toasts, not just Telegram's and Windows' own.</summary>
    public bool IncludeAllApps { get; set; }

    public async Task StartAsync()
    {
        if (await _listener.RequestAccessAsync() != UserNotificationListenerAccessStatus.Allowed)
        {
            Debug.WriteLine("Notification access denied; notifications disabled.");
            return;
        }

        await PollAsync();
        _poll.Start();
    }

    public void Dispose() => _poll.Stop();

    /// <summary>
    /// _seen is set only once access was granted. On resume, the newest toast that came in during the game still shows.
    /// </summary>
    public void SetPaused(bool paused) => _poll.IsEnabled = !paused && _seen is not null;

    private static bool IsTelegram(AppInfo app) =>
        app.AppUserModelId.Contains("Telegram", StringComparison.OrdinalIgnoreCase)
        || app.DisplayInfo.DisplayName.Contains("Telegram", StringComparison.OrdinalIgnoreCase);

    // Windows' own toasts (e.g. Windows.Defender.SecurityCenter, Windows.SystemToast.*); third-party apps are skipped.
    private static bool IsSystem(AppInfo app) =>
        app.AppUserModelId.StartsWith("Windows.", StringComparison.OrdinalIgnoreCase)
        || app.AppUserModelId.StartsWith("Microsoft.Windows.", StringComparison.OrdinalIgnoreCase);

    private async Task PollAsync()
    {
        if (_isPolling)
        {
            return;
        }

        _isPolling = true;
        try
        {
            var notifications = await _listener.GetNotificationsAsync(NotificationKinds.Toast);
            var current = new HashSet<uint>();
            UserNotification? newest = null;
            foreach (var n in notifications)
            {
                if (n.AppInfo is not { } app || !(IncludeAllApps || IsTelegram(app) || IsSystem(app)))
                {
                    continue;
                }

                current.Add(n.Id);
                if (_seen is not null && !_seen.Contains(n.Id) && (newest is null || n.CreationTime > newest.CreationTime))
                {
                    newest = n;
                }
            }

            // First poll only records what's already in the Action Center.
            _seen = current;
            if (newest is not null)
            {
                await ShowAsync(newest);
            }
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"Reading notifications failed: {ex.Message}");
        }
        finally
        {
            _isPolling = false;
        }
    }

    private async Task ShowAsync(UserNotification notification)
    {
        var app = notification.AppInfo;
        var texts = notification.Notification.Visual
            .GetBinding(KnownNotificationBindings.ToastGeneric)?
            .GetTextElements()
            .Select(t => t.Text)
            .Where(t => !string.IsNullOrWhiteSpace(t))
            .ToList() ?? [];

        var isTelegram = IsTelegram(app);
        Activity.AppLogo = isTelegram ? null : await LoadLogoAsync(app);
        Activity.IsTelegram = isTelegram;
        Activity.Sender = texts.FirstOrDefault() ?? app.DisplayInfo.DisplayName;
        Activity.Text = string.Join(" · ", texts.Skip(1));
        _coordinator.Flash(Activity, DisplayDuration);
    }

    private static async Task<ImageSource?> LoadLogoAsync(AppInfo app)
    {
        try
        {
            return await ArtworkLoader.LoadAsync(app.DisplayInfo.GetLogo(new Windows.Foundation.Size(64, 64)));
        }
        catch (COMException)
        {
            return null; // Some senders (e.g. Windows Security) have no logo; the template shows a bell glyph.
        }
    }

    private void Open()
    {
        _coordinator.Hide(Activity);

        // Windows doesn't expose a toast's launch arguments to listeners, so only Telegram can be opened.
        if (Activity.IsTelegram)
        {
            OpenTelegram();
        }
    }

    /// <summary>Re-launching Telegram's exe makes the running (single-instance) app show its window, even from the tray.</summary>
    private static void OpenTelegram()
    {
        NativeMethods.AllowAnyForeground();
        try
        {
            Process.Start(new ProcessStartInfo(FindTelegramPath() ?? "tg://") { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            Debug.WriteLine($"Could not open Telegram: {ex.Message}");
        }
    }

    private static string? FindTelegramPath()
    {
        foreach (var process in Process.GetProcessesByName("Telegram"))
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path)
                    {
                        return path;
                    }
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // Store build lives under WindowsApps and may deny access; fall back to tg://.
                }
            }
        }

        return null;
    }
}
