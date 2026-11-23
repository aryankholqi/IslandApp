using System.Windows.Threading;
using IslandApp.Interop;

namespace IslandApp.Services;

/// <summary>
/// Tells when a full-screen app (usually a game) is running, so the island can get out of the way. Polled: a game
/// can go full screen without the foreground window changing, and Windows has no event for it.
/// </summary>
public sealed class FullScreenService : IDisposable
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromSeconds(1) };

    public FullScreenService() => _poll.Tick += (_, _) => Update();

    public event EventHandler? Changed;

    public bool IsActive { get; private set; }

    public void Start()
    {
        Update();
        _poll.Start();
    }

    public void Dispose() => _poll.Stop();

    private void Update()
    {
        var active = NativeMethods.IsFullScreenAppRunning();
        if (active != IsActive)
        {
            IsActive = active;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}
