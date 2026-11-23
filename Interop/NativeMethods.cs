using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace IslandApp.Interop;

/// <summary>A display in physical pixels. <see cref="DeviceName"/> (e.g. \\.\DISPLAY2) identifies it in settings.</summary>
internal readonly record struct DisplayMonitor(string DeviceName, int Left, int Top, int Width, int Height, bool IsPrimary);

internal static partial class NativeMethods
{
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080;   // hidden from Alt+Tab
    private const long WsExNoActivate = 0x08000000;   // clicks never steal focus from the user's app
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaCaptionColor = 35;         // Windows 11 only; ignored on Windows 10
    private const uint EventSystemForeground = 0x0003;
    private const uint RegNotifyChangeName = 0x1;     // subkeys added or removed
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private const uint MonitorInfoPrimary = 0x1;
    private const uint RegNotifyChangeLastSet = 0x4;  // values changed
    private const uint WinEventOutOfContext = 0x0000;

    public static void ApplyOverlayStyle(IntPtr hwnd)
    {
        var style = GetWindowLongPtr(hwnd, GwlExStyle);
        SetWindowLongPtr(hwnd, GwlExStyle, (IntPtr)((long)style | WsExToolWindow | WsExNoActivate));
    }

    /// <summary>Dark title bar whose caption blends into the window background.</summary>
    public static void UseDarkTitleBar(IntPtr hwnd, byte r, byte g, byte b)
    {
        var enabled = 1;
        DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref enabled, sizeof(int));
        var colorRef = r | (g << 8) | (b << 16); // COLORREF is 0x00BBGGRR
        DwmSetWindowAttribute(hwnd, DwmwaCaptionColor, ref colorRef, sizeof(int));
    }

    public delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint thread, uint time);

    /// <summary>Calls <paramref name="callback"/> on this thread's message loop whenever the foreground window changes.</summary>
    public static IntPtr HookForegroundChanges(WinEventProc callback) =>
        SetWinEventHook(EventSystemForeground, EventSystemForeground, IntPtr.Zero, callback, 0, 0, WinEventOutOfContext);

    public static void Unhook(IntPtr hook) => UnhookWinEvent(hook);

    public delegate IntPtr LowLevelMouseProc(int code, IntPtr message, IntPtr data);

    /// <summary>Sees every mouse event system-wide, on this thread's message loop. Keep the callback fast: Windows waits on it.</summary>
    public static IntPtr HookMouse(LowLevelMouseProc callback) =>
        SetWindowsHookEx(14 /* WH_MOUSE_LL */, callback, GetModuleHandle(IntPtr.Zero), 0);

    public static void UnhookMouse(IntPtr hook) => UnhookWindowsHookEx(hook);

    public static IntPtr NextMouseHook(int code, IntPtr message, IntPtr data) => CallNextHookEx(IntPtr.Zero, code, message, data);

    /// <summary>True for a left, right or middle button press.</summary>
    public static bool IsButtonDown(IntPtr message) => (int)message is 0x201 or 0x204 or 0x207;

    // DllImport (not LibraryImport): the callback is a managed delegate.
    [DllImport("user32.dll")]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW")]
    private static partial IntPtr GetModuleHandle(IntPtr moduleName);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(IntPtr handle);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static unsafe partial int GetPackageFamilyName(IntPtr process, ref uint length, char* name);

    /// <summary>A packaged (Store) app's family name, e.g. "Microsoft.ZuneMusic_8wekyb3d8bbwe"; null for desktop apps.</summary>
    public static unsafe string? GetProcessPackageFamilyName(uint processId)
    {
        const uint QueryLimitedInformation = 0x1000;
        var process = OpenProcess(QueryLimitedInformation, false, processId);
        if (process == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[256];
            uint length = 256;
            return GetPackageFamilyName(process, ref length, buffer) == 0 ? new string(buffer, 0, (int)length - 1) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public IntPtr Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(IntPtr icon);

    /// <summary>The 32 px icon Explorer shows for a file or folder, or null.</summary>
    public static System.Windows.Media.ImageSource? GetFileIcon(string path)
    {
        const uint Icon = 0x100, LargeIcon = 0x0;
        var info = default(ShFileInfo);
        if (SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), Icon | LargeIcon) == IntPtr.Zero || info.Icon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var image = System.Windows.Interop.Imaging.CreateBitmapSourceFromHIcon(
                info.Icon, System.Windows.Int32Rect.Empty, System.Windows.Media.Imaging.BitmapSizeOptions.FromEmptyOptions());
            image.Freeze();
            return image;
        }
        finally
        {
            DestroyIcon(info.Icon);
        }
    }

    public static int GetWindowProcessId(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return (int)processId;
    }

    [LibraryImport("user32.dll")]
    public static partial IntPtr GetForegroundWindow();

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    /// <summary>All displays, left to right.</summary>
    public static List<DisplayMonitor> GetMonitors()
    {
        var monitors = new List<DisplayMonitor>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (handle, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
            if (GetMonitorInfo(handle, ref info))
            {
                var r = info.Monitor;
                monitors.Add(new DisplayMonitor(info.DeviceName, r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top, (info.Flags & MonitorInfoPrimary) != 0));
            }

            return true;
        }, IntPtr.Zero);
        return [.. monitors.OrderBy(m => m.Left).ThenBy(m => m.Top)];
    }

    /// <summary>Moves a window to physical-pixel coordinates without resizing or activating it.</summary>
    public static void MoveWindow(IntPtr hwnd, int x, int y) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, 0, 0, SwpNoSize | SwpNoZOrder | SwpNoActivate);

    /// <summary>
    /// Whether Windows sees a full-screen app (a game, exclusive or borderless, a full-screen video or a presentation):
    /// the same signal that hides the taskbar and drives "don't disturb while gaming".
    /// </summary>
    public static bool IsFullScreenAppRunning()
    {
        // The desktop covers the whole monitor, so Windows can call it "busy" too; it is never a game.
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero || IsShellWindow(hwnd))
        {
            return false;
        }

        return (SHQueryUserNotificationState(out var state) == 0 && state is QunsBusy or QunsRunningD3DFullScreen or QunsPresentationMode)
            || IsCoveringMonitor(hwnd); // Windows answers "quiet time" for a while after setup or an update, hiding the above
    }

    /// <summary>The desktop or a taskbar.</summary>
    private static bool IsShellWindow(IntPtr hwnd)
    {
        if (hwnd == GetShellWindow())
        {
            return true;
        }

        Span<char> name = stackalloc char[32];
        int length;
        unsafe
        {
            fixed (char* buffer = name)
            {
                length = GetClassName(hwnd, buffer, name.Length);
            }
        }

        return name[..Math.Max(0, length)] is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd";
    }

    /// <summary>The window covers its whole monitor, taskbar area too (a maximized window doesn't).</summary>
    private static bool IsCoveringMonitor(IntPtr hwnd)
    {
        if (GetWindowProcessId(hwnd) == Environment.ProcessId || !GetWindowRect(hwnd, out var window))
        {
            return false;
        }

        var info = new MonitorInfoEx { Size = Marshal.SizeOf<MonitorInfoEx>() };
        if (!GetMonitorInfo(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info))
        {
            return false;
        }

        var m = info.Monitor;
        return window.Left <= m.Left && window.Top <= m.Top && window.Right >= m.Right && window.Bottom >= m.Bottom;
    }

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetShellWindow();

    private const uint MonitorDefaultToNearest = 2;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [LibraryImport("user32.dll")]
    private static partial IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassName(IntPtr hwnd, char* className, int maxCount);

    private const int QunsBusy = 2;                  // a full-screen app has the foreground
    private const int QunsRunningD3DFullScreen = 3;  // exclusive Direct3D full screen
    private const int QunsPresentationMode = 4;

    [LibraryImport("shell32.dll")]
    private static partial int SHQueryUserNotificationState(out int state);

    /// <summary>Moves and resizes a window in physical pixels without activating it.</summary>
    public static void PlaceWindow(IntPtr hwnd, int x, int y, int width, int height) =>
        SetWindowPos(hwnd, IntPtr.Zero, x, y, width, height, SwpNoZOrder | SwpNoActivate);

    /// <summary>Clips a window to a rounded rectangle (physical pixels); works on windows that aren't layered.</summary>
    public static void RoundCorners(IntPtr hwnd, int width, int height, int radius) =>
        SetWindowRgn(hwnd, CreateRoundRectRgn(0, 0, width + 1, height + 1, radius * 2, radius * 2), redraw: true);

    [LibraryImport("gdi32.dll")]
    private static partial IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    // The system owns the region after this call, so it is never deleted here.
    [LibraryImport("user32.dll")]
    private static partial int SetWindowRgn(IntPtr hwnd, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public int Size;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    private delegate bool WindowEnumProc(IntPtr hwnd, IntPtr data);

    /// <summary>Top-level dialog windows (class #32770) owned by any of <paramref name="processIds"/>.</summary>
    public static List<IntPtr> GetDialogs(IReadOnlyCollection<int> processIds)
    {
        var dialogs = new List<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (processIds.Contains(GetWindowProcessId(hwnd)) && GetClassName(hwnd) == "#32770")
            {
                dialogs.Add(hwnd);
            }

            return true;
        }, IntPtr.Zero);
        return dialogs;
    }

    /// <summary>Text of the first control with <paramref name="controlId"/> anywhere inside <paramref name="parent"/>, or null.</summary>
    public static string? GetDescendantText(IntPtr parent, int controlId)
    {
        string? text = null;
        EnumChildWindows(parent, (child, _) =>
        {
            if (GetDlgCtrlID(child) != controlId)
            {
                return true;
            }

            text = GetWindowTitle(child);
            return false;
        }, IntPtr.Zero);
        return text;
    }

    /// <summary>
    /// Reads the list view <paramref name="controlId"/> of another process's <paramref name="parent"/>: the texts of
    /// <paramref name="columns"/> in the first row whose <c>columns[0]</c> text satisfies <paramref name="match"/>, or null.
    /// A list view only copies text into its own process's memory, so a buffer is borrowed there and read back.
    /// </summary>
    public static string[]? FindListViewRow(IntPtr parent, int controlId, Func<string, bool> match, params int[] columns)
    {
        var list = GetDlgItem(parent, controlId);
        if (list == IntPtr.Zero)
        {
            return null;
        }

        var process = OpenProcess(VmOperation | VmRead | VmWrite | 0x1000 /* query limited info, for IsWow64Process */, false, (uint)GetWindowProcessId(list));
        if (process == IntPtr.Zero)
        {
            return null;
        }

        var remote = VirtualAllocEx(process, IntPtr.Zero, 4096, MemCommitReserve, PageReadWrite);
        try
        {
            if (remote == IntPtr.Zero || !IsWow64Process(process, out var is32Bit))
            {
                return null;
            }

            string? Cell(int row, int column)
            {
                // LVITEMW: iSubItem at 8; pszText/cchTextMax at 20/24 in a 32-bit process, 24/32 in a 64-bit one.
                Span<byte> item = stackalloc byte[88];
                item.Clear();
                BitConverter.TryWriteBytes(item[8..], column);
                var text = remote + 512;
                if (is32Bit)
                {
                    BitConverter.TryWriteBytes(item[20..], (int)text);
                    BitConverter.TryWriteBytes(item[24..], 1024);
                }
                else
                {
                    BitConverter.TryWriteBytes(item[24..], (long)text);
                    BitConverter.TryWriteBytes(item[32..], 1024);
                }

                if (!WriteProcessMemory(process, remote, item, item.Length, out _)
                    || SendMessageTimeout(list, LvmGetItemText, row, remote, SmtoAbortIfHung, 200, out var length) == IntPtr.Zero)
                {
                    return null;
                }

                var chars = new char[(int)length];
                return ReadProcessMemory(process, text, System.Runtime.InteropServices.MemoryMarshal.AsBytes(chars.AsSpan()), chars.Length * 2, out _)
                    ? new string(chars)
                    : null;
            }

            SendMessageTimeout(list, LvmGetItemCount, 0, IntPtr.Zero, SmtoAbortIfHung, 200, out var count);
            for (var row = 0; row < (int)count; row++)
            {
                if (Cell(row, columns[0]) is { } first && match(first))
                {
                    return [first, .. columns[1..].Select(c => Cell(row, c) ?? string.Empty)];
                }
            }

            return null;
        }
        finally
        {
            if (remote != IntPtr.Zero)
            {
                VirtualFreeEx(process, remote, 0, MemRelease);
            }

            CloseHandle(process);
        }
    }

    private const uint VmOperation = 0x8, VmRead = 0x10, VmWrite = 0x20;
    private const uint MemCommitReserve = 0x3000, MemRelease = 0x8000, PageReadWrite = 4;
    private const uint LvmGetItemCount = 0x1004, LvmGetItemText = 0x1073, SmtoAbortIfHung = 2;

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetDlgItem(IntPtr dialog, int controlId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(IntPtr hwnd);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial IntPtr SendMessageTimeout(IntPtr hwnd, uint message, nint wParam, IntPtr lParam, uint flags, uint timeout, out nint result);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWow64Process(IntPtr process, [MarshalAs(UnmanagedType.Bool)] out bool isWow64);

    [LibraryImport("kernel32.dll")]
    private static partial IntPtr VirtualAllocEx(IntPtr process, IntPtr address, nint size, uint type, uint protect);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool VirtualFreeEx(IntPtr process, IntPtr address, nint size, uint type);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool WriteProcessMemory(IntPtr process, IntPtr address, ReadOnlySpan<byte> buffer, nint size, out nint written);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadProcessMemory(IntPtr process, IntPtr address, Span<byte> buffer, nint size, out nint read);

    /// <summary>A window's text; works across processes for captions and static controls.</summary>
    public static string GetWindowTitle(IntPtr hwnd)
    {
        Span<char> buffer = stackalloc char[512];
        int length;
        unsafe
        {
            fixed (char* chars = buffer)
            {
                length = GetWindowText(hwnd, chars, buffer.Length);
            }
        }

        return new string(buffer[..Math.Max(0, length)]);
    }

    private static string GetClassName(IntPtr hwnd)
    {
        Span<char> name = stackalloc char[64];
        int length;
        unsafe
        {
            fixed (char* buffer = name)
            {
                length = GetClassName(hwnd, buffer, name.Length);
            }
        }

        return new string(name[..Math.Max(0, length)]);
    }

    /// <summary>The first top-level window of a class, whatever its title.</summary>
    public static IntPtr FindWindowByClass(string className) => FindWindow(className, null);

    /// <summary>Input language (LANGID) of the foreground window's keyboard layout, or 0 with no foreground window.</summary>
    public static int GetForegroundInputLanguage()
    {
        var hwnd = GetForegroundWindow();
        return hwnd == IntPtr.Zero ? 0 : (int)((long)GetKeyboardLayout(GetWindowThreadProcessId(hwnd, out _)) & 0xFFFF);
    }

    public static bool IsCapsLockOn() => (GetKeyState(0x14) & 1) != 0; // VK_CAPITAL; low bit = toggled

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll")]
    private static partial IntPtr GetKeyboardLayout(uint threadId);

    [LibraryImport("user32.dll")]
    private static partial short GetKeyState(int virtualKey);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW")]
    private static unsafe partial int GetWindowText(IntPtr hwnd, char* text, int maxCount);

    [LibraryImport("user32.dll")]
    private static partial int GetDlgCtrlID(IntPtr hwnd);

    // DllImport (not LibraryImport): the callback is a managed delegate.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(WindowEnumProc callback, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumChildWindows(IntPtr parent, WindowEnumProc callback, IntPtr data);

    // DllImport: a delegate callback and a by-value string struct, which LibraryImport doesn't marshal.
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfoEx info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    /// <summary>Signals <paramref name="changed"/> once when anything under <paramref name="key"/> changes. Returns a Win32 error code.</summary>
    public static int NotifyRegistryChange(SafeRegistryHandle key, SafeWaitHandle changed) =>
        RegNotifyChangeKeyValue(key, watchSubtree: true, RegNotifyChangeName | RegNotifyChangeLastSet, changed, asynchronous: true);

    [LibraryImport("advapi32.dll")]
    private static partial int RegNotifyChangeKeyValue(
        SafeRegistryHandle key,
        [MarshalAs(UnmanagedType.Bool)] bool watchSubtree,
        uint filter,
        SafeWaitHandle eventHandle,
        [MarshalAs(UnmanagedType.Bool)] bool asynchronous);

    /// <summary>Lets the next process we launch take the foreground (our island is WS_EX_NOACTIVATE).</summary>
    public static void AllowAnyForeground() => AllowSetForegroundWindow(-1);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AllowSetForegroundWindow(int dwProcessId);

    // DllImport (not LibraryImport): the callback is a managed delegate.
    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWinEvent(IntPtr hook);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static partial IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);
}
