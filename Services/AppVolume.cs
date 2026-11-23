using System.Diagnostics;
using System.Runtime.InteropServices;
using IslandApp.Interop;

namespace IslandApp.Services;

/// <summary>
/// An app's own volume (its Volume Mixer slider), not the Windows master volume. The app is matched by the media
/// session's app id, which names its process ("Spotify.exe", "Chrome", "MSEdge"…).
/// shortcut: a browser's level covers all its tabs, and apps whose id doesn't name the process (Firefox) get no
/// slider; per-tab or in-player volume would need each site's own API.
/// </summary>
internal static class AppVolume
{
    /// <summary>The app's level (0–1), or null when it has no audio session to control.</summary>
    public static float? Get(string appId)
    {
        float? level = null;
        ForEachSession(appId, volume =>
        {
            if (level is null && volume.GetMasterVolume(out var value) == 0)
            {
                level = value;
            }
        });
        return level;
    }

    public static void Set(string appId, float level)
    {
        var context = Guid.Empty;
        ForEachSession(appId, volume => volume.SetMasterVolume(Math.Clamp(level, 0, 1), ref context));
    }

    // Browsers play from several processes, so every matching session gets the call.
    private static void ForEachSession(string appId, Action<ISimpleAudioVolume> action)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioSessionManager2? manager = null;
        IAudioSessionEnumerator? sessions = null;
        try
        {
            enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            if (enumerator.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device) != 0 || device is null)
            {
                return;
            }

            var iid = typeof(IAudioSessionManager2).GUID;
            if (device.Activate(ref iid, CoreAudio.ClsCtxAll, IntPtr.Zero, out var instance) != 0)
            {
                return;
            }

            manager = (IAudioSessionManager2)instance;
            if (manager.GetSessionEnumerator(out sessions) != 0 || sessions is null || sessions.GetCount(out var count) != 0)
            {
                return;
            }

            for (var i = 0; i < count; i++)
            {
                if (sessions.GetSession(i, out var session) != 0 || session is null)
                {
                    continue;
                }

                try
                {
                    // >= 0, not == 0: Media Player's session answers AUDCLNT_S_NO_SINGLE_PROCESS (a success code) yet still gives its pid.
                    if (session.GetProcessId(out var pid) >= 0 && Matches(appId, pid) && session is ISimpleAudioVolume volume)
                    {
                        action(volume);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(session);
                }
            }
        }
        catch (COMException ex)
        {
            Debug.WriteLine($"App volume unavailable: {ex.Message}");
        }
        finally
        {
            foreach (var com in new object?[] { sessions, manager, device, enumerator })
            {
                if (com is not null)
                {
                    Marshal.ReleaseComObject(com);
                }
            }
        }
    }

    private static bool Matches(string appId, uint pid)
    {
        if (pid == 0)
        {
            return false; // system sounds
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            var name = process.ProcessName;
            if (name.Length > 2 && appId.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Store apps (Media Player: "Microsoft.ZuneMusic_8wekyb3d8bbwe!…" runs as Microsoft.Media.Player.exe)
            // are named by package, not process.
            return appId.Contains('!') && NativeMethods.GetProcessPackageFamilyName(pid) is { } family
                && appId.StartsWith(family + "!", StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false; // exited meanwhile
        }
    }
}
