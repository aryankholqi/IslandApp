using System.Diagnostics;
using System.Windows.Threading;
using IslandApp.Interop;

namespace IslandApp.Services;

/// <summary>
/// Tracks whether the foreground window belongs to an IDE, via a system foreground hook (no polling).
/// Leaving an IDE is debounced so Alt+Tab or a click on the taskbar doesn't make the island flicker.
/// </summary>
public sealed class ForegroundAppService : IDisposable
{
    private static readonly TimeSpan LeaveDelay = TimeSpan.FromSeconds(1);

    private readonly NativeMethods.WinEventProc _callback; // kept alive for the hook's lifetime
    private readonly DispatcherTimer _leaveTimer = new() { Interval = LeaveDelay };
    private readonly int _ownProcessId = Environment.ProcessId;
    private IntPtr _hook;
    private HashSet<string> _ideProcesses = new(StringComparer.OrdinalIgnoreCase);

    public ForegroundAppService()
    {
        _callback = (_, _, hwnd, _, _, _, _) => Update(hwnd);
        _leaveTimer.Tick += (_, _) =>
        {
            _leaveTimer.Stop();
            SetIdeFocused(false);
        };
    }

    public event EventHandler? IdeFocusChanged;

    public bool IsIdeFocused { get; private set; }

    /// <summary>Process names without ".exe" (from Island Settings). Re-checks the current foreground window.</summary>
    public void SetIdeProcesses(IEnumerable<string> names)
    {
        _ideProcesses = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        if (_hook != IntPtr.Zero)
        {
            Update(NativeMethods.GetForegroundWindow());
        }
    }

    /// <summary>Must be called on the UI thread: the hook's callbacks arrive through its message loop.</summary>
    public void Start()
    {
        _hook = NativeMethods.HookForegroundChanges(_callback);
        Update(NativeMethods.GetForegroundWindow());
    }

    public void Dispose()
    {
        _leaveTimer.Stop();
        if (_hook != IntPtr.Zero)
        {
            NativeMethods.Unhook(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private void Update(IntPtr hwnd)
    {
        var processId = NativeMethods.GetWindowProcessId(hwnd);

        // Our own windows (context menu, settings) shouldn't count as leaving the IDE.
        if (processId == 0 || processId == _ownProcessId)
        {
            return;
        }

        if (IsIde(processId))
        {
            _leaveTimer.Stop();
            SetIdeFocused(true);
        }
        else if (IsIdeFocused && !_leaveTimer.IsEnabled)
        {
            _leaveTimer.Start();
        }
    }

    private void SetIdeFocused(bool value)
    {
        if (value != IsIdeFocused)
        {
            IsIdeFocused = value;
            IdeFocusChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private bool IsIde(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return _ideProcesses.Contains(process.ProcessName);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false; // exited in the meantime
        }
    }
}
