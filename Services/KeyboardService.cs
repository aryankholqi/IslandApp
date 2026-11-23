using System.Globalization;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using IslandApp.Interop;

namespace IslandApp.Services;

/// <summary>
/// Flashes the island when the input language (e.g. EN ↔ FA) or Caps Lock changes. Polled: Windows only tells the
/// window whose layout changed, not other apps.
/// shortcut: with "a different input method for each app window" on, switching windows can flash too; it's off by default.
/// </summary>
public sealed class KeyboardService : IDisposable
{
    private const string KeyboardGlyph = "";

    private readonly ActivityCoordinator _coordinator;
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private int _language;
    private bool _capsLock;

    public KeyboardService(ActivityCoordinator coordinator)
    {
        _coordinator = coordinator;
        _poll.Tick += (_, _) => Update();
    }

    public TimeSpan DisplayDuration { get; set; } = TimeSpan.FromSeconds(1.5);

    public StatusActivity Activity { get; } = new();

    public void Start()
    {
        _language = NativeMethods.GetForegroundInputLanguage();
        _capsLock = NativeMethods.IsCapsLockOn();
        _poll.Start();
    }

    public void Dispose() => _poll.Stop();

    /// <summary>Resuming re-reads the state, so Caps Lock or a layout switch in a game doesn't flash afterwards.</summary>
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
        var capsLock = NativeMethods.IsCapsLockOn();
        if (capsLock != _capsLock)
        {
            _capsLock = capsLock;
            Activity.Update(KeyboardGlyph, "Caps Lock", capsLock ? "On" : "Off", capsLock ? ActivityTone.Accent : ActivityTone.Neutral);
            _coordinator.Flash(Activity, DisplayDuration);
        }

        var language = NativeMethods.GetForegroundInputLanguage();
        if (language == 0 || language == _language)
        {
            return;
        }

        _language = language;
        var (code, name) = Describe(language);
        Activity.Update(KeyboardGlyph, code, name, ActivityTone.Neutral);
        _coordinator.Flash(Activity, DisplayDuration);
    }

    /// <summary>0x0429 → ("FA", "فارسی"); 0x0409 → ("EN", "English").</summary>
    private static (string Code, string Name) Describe(int languageId)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageId);
            var name = culture.NativeName;
            var paren = name.IndexOf(" (", StringComparison.Ordinal);
            return (culture.TwoLetterISOLanguageName.ToUpperInvariant(), paren > 0 ? name[..paren] : name);
        }
        catch (CultureNotFoundException)
        {
            return ("??", languageId.ToString("X4", CultureInfo.InvariantCulture));
        }
    }
}
