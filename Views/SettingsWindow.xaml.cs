using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using IslandApp.Activities;
using IslandApp.Core;
using IslandApp.Interop;

namespace IslandApp.Views;

/// <summary>The "Island Settings" app: a normal window over the running island's <see cref="AppSettings"/>, so changes apply live.</summary>
public partial class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings, ClaudeActivity claude)
    {
        InitializeComponent();
        DataContext = settings;
        ClaudeUsagePanel.DataContext = claude;
        VersionText.Text = $"Version {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        MaxHeight = SystemParameters.WorkArea.Height * 0.9; // scrolls beyond this

        // Displays are listed when the window opens; "Main display" follows whichever is primary.
        var monitors = NativeMethods.GetMonitors();
        MonitorPicker.ItemsSource = monitors.Count < 2
            ? [new Option("Main", string.Empty)]
            : monitors
                .Select((m, i) => new Option($"{i + 1}{(m.IsPrimary ? " · main" : string.Empty)}", m.DeviceName))
                .Prepend(new Option("Main", string.Empty))
                .ToList();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var background = (Color)FindResource("WindowColor");
        NativeMethods.UseDarkTitleBar(new WindowInteropHelper(this).Handle, background.R, background.G, background.B);
    }
}
