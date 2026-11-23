using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows.Controls;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using IslandApp.Activities;
using IslandApp.Core;
using IslandApp.Interop;
using IslandApp.ViewModels;
using Microsoft.Win32;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace IslandApp.Views;

/// <summary>
/// Hosts the island. The view owns only presentation concerns: morphing the pill to the
/// current activity's size, glow colour, hover feedback and auto-collapse.
/// </summary>
public partial class IslandWindow : Window
{
    private const double ExpandedCornerRadius = 30;
    private const double PillMaxHeight = 44;
    private const double HoverScaleFactor = 1.03;

    private static readonly Size IdleSize = new(126, 34);
    private const double ContentStartScale = 0.94;
    private const double BubbleSize = 36;
    private const double BubbleGap = 8;
    private const double BubblePeekPadding = 14; // keeps the last text clear of the pill's rounded end
    private const double TopMargin = 8;
    // Room for the largest states (expanded media + bubble, the video) plus the glow's blur on every side. Transparent pixels are click-through.
    private const double BaseWidth = 560;
    private const double BaseHeight = 340;
    private const double VideoCornerRadius = ExpandedCornerRadius; // the video fills the island edge to edge
    private const string TimerItemTag = "timer";

    private static readonly Duration HoverDuration = new(TimeSpan.FromMilliseconds(160));
    private static readonly IEasingFunction MorphEasing = Frozen(new SpringEase());
    private static readonly IEasingFunction HoverEasing = Frozen(new QuadraticEase { EasingMode = EasingMode.EaseOut });
    private static readonly IEasingFunction ContentEasing = Frozen(new CubicEase { EasingMode = EasingMode.EaseOut });

    private readonly IslandViewModel _viewModel;
    private readonly DispatcherTimer _collapseTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private readonly Color _positiveGlow;
    private readonly DispatcherTimer _peekTimer = new() { Interval = TimeSpan.FromSeconds(1.5) };
    private bool _isPeeking;
    private bool _peekAutoClose = true;
    private IslandActivity? _activity;

    // The pill's shape, driven per frame by StepSprings. Start values match the XAML.
    private readonly Spring _widthSpring = new(IdleSize.Width);
    private readonly Spring _heightSpring = new(IdleSize.Height);
    private readonly Spring _radiusSpring = new(IdleSize.Height / 2);
    private Size _morphedSize = IdleSize;
    private bool _springing;
    private TimeSpan? _lastFrame;

    // Collapses on a click outside the island; hooked only while expanded. The delegate is kept alive for the hook.
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private IntPtr _mouseHook;

    // The video plays in its own window over the island's video slot (see VideoActivity).
    private readonly DispatcherTimer _videoPlaceTimer = new();
    private VideoActivity? _video;
    private VideoWindow? _videoWindow;
    private string? _loadedVideoId;
    private bool _followingIsland;
    private Int32Rect _placedVideo;

    // From Island Settings (ApplySettings).
    private Duration _morphDuration = new(TimeSpan.FromMilliseconds(600));
    private TimeSpan _contentDelay = TimeSpan.FromMilliseconds(110);
    private TimeSpan _contentFade = TimeSpan.FromMilliseconds(320);
    private bool _autoCollapse = true;
    private string _monitorName = string.Empty;
    private double _topOffset;
    private IReadOnlyList<int> _timerPresets = [5, 25];

    public IslandWindow(IslandViewModel viewModel)
    {
        InitializeComponent();

        _viewModel = viewModel;
        DataContext = viewModel;
        _positiveGlow = (Color)FindResource("PunkColor");

        _mouseProc = OnGlobalMouse;
        _collapseTimer.Tick += OnCollapseTimerTick;
        _peekTimer.Tick += (_, _) => SetPeek(false);
        _videoPlaceTimer.Tick += (_, _) =>
        {
            _videoPlaceTimer.Stop();
            PlaceVideo();
        };
        viewModel.Coordinator.PropertyChanged += OnCoordinatorPropertyChanged;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        NativeMethods.ApplyOverlayStyle(new WindowInteropHelper(this).Handle);
        Reposition();
    }

    /// <summary>Applies the appearance, position and behaviour settings. Clamps values: the settings file is hand-editable.</summary>
    public void ApplySettings(AppSettings settings)
    {
        var speed = Math.Clamp(Finite(settings.AnimationSpeed, 1), 0.4, 2.5);
        _morphDuration = new Duration(TimeSpan.FromMilliseconds(600 * speed));
        _contentDelay = TimeSpan.FromMilliseconds(110 * speed);
        _contentFade = TimeSpan.FromMilliseconds(320 * speed);

        var glow = Math.Clamp(Finite(settings.GlowOpacity, 0.45), 0, 1);
        Glow.Opacity = glow;
        Pill.Effect = glow > 0 ? Glow : null; // no effect at all when off: cheaper to render

        var collapse = Finite(settings.CollapseSeconds, 1.5);
        _autoCollapse = collapse > 0;
        _collapseTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(collapse, 0.3, 60));

        var bubble = Finite(settings.BubbleCollapseSeconds, 1.5);
        _peekAutoClose = bubble > 0;
        _peekTimer.Interval = TimeSpan.FromSeconds(Math.Clamp(bubble, 0.3, 60));
        _peekTimer.Stop();
        if (_isPeeking && _peekAutoClose)
        {
            _peekTimer.Start(); // an open bubble follows the new delay
        }

        var scale = Math.Clamp(Finite(settings.Scale, 1), 0.6, 1.6);
        RootScale.ScaleX = RootScale.ScaleY = scale;
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;

        _monitorName = settings.Monitor ?? string.Empty;
        _topOffset = Math.Clamp(Finite(settings.TopOffset, 0), 0, 200);
        _timerPresets = settings.TimerPresetMinutes();

        Reposition();
        Morph(); // picks up a new accent glow and the new speed
        UpdateVideo(); // the island may have moved or resized
    }

    private static double Finite(double value, double fallback) => double.IsFinite(value) ? value : fallback;

    /// <summary>
    /// Fades the island (and a video playing in it) out while a full-screen app such as a game runs, then hides it so
    /// it takes no clicks; fades back in afterwards. ShowActivated is off, so coming back never steals the game's focus.
    /// </summary>
    public void SetHiddenForFullScreen(bool hidden)
    {
        var fade = new DoubleAnimation(hidden ? 0 : 1, HoverDuration);
        if (hidden)
        {
            _videoWindow?.Hide(); // its own window: it doesn't follow the island's opacity
            fade.Completed += (_, _) =>
            {
                if (Opacity == 0)
                {
                    Hide();
                }
            };
        }
        else
        {
            Show();
            _videoWindow?.Show();
        }

        BeginAnimation(OpacityProperty, fade);
    }

    protected override void OnClosed(EventArgs e)
    {
        // SystemEvents is static: unsubscribe or the window leaks.
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _viewModel.Coordinator.PropertyChanged -= OnCoordinatorPropertyChanged;
        _collapseTimer.Stop();
        _peekTimer.Stop();
        _videoPlaceTimer.Stop();
        CompositionTarget.Rendering -= FollowIsland; // static event: unhook or the window leaks
        CompositionTarget.Rendering -= StepSprings;
        HookOutsideClicks(false);
        Track(null);
        base.OnClosed(e);
    }

    /// <summary>
    /// Centres the window at the top of the chosen display (the main one if it's gone). Works in physical pixels:
    /// move onto the display first so WPF adopts its DPI, then centre using that DPI.
    /// </summary>
    private void Reposition()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero)
        {
            return; // OnSourceInitialized calls this again
        }

        var monitors = NativeMethods.GetMonitors();
        if (monitors.Count == 0)
        {
            return;
        }

        var monitor = monitors.FirstOrDefault(m => m.DeviceName == _monitorName);
        if (monitor.DeviceName is null)
        {
            monitor = monitors.FirstOrDefault(m => m.IsPrimary);
        }

        if (monitor.DeviceName is null)
        {
            monitor = monitors[0];
        }

        NativeMethods.MoveWindow(hwnd, monitor.Left, monitor.Top);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var widthPx = (int)Math.Round(Width * dpi);
        NativeMethods.MoveWindow(hwnd, monitor.Left + ((monitor.Width - widthPx) / 2), monitor.Top + (int)Math.Round(_topOffset * dpi));
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(Reposition);

    /// <summary>Rebuilds the timer presets at the top of the menu from the settings.</summary>
    private void OnIslandContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        var items = Island.ContextMenu.Items;
        foreach (var old in items.OfType<MenuItem>().Where(item => Equals(item.Tag, TimerItemTag)).ToList())
        {
            items.Remove(old);
        }

        for (var i = 0; i < _timerPresets.Count; i++)
        {
            var minutes = _timerPresets[i].ToString(CultureInfo.InvariantCulture);
            items.Insert(i, new MenuItem
            {
                Header = $"Timer · {minutes} min",
                Command = _viewModel.StartTimerCommand,
                CommandParameter = minutes,
                Tag = TimerItemTag,
            });
        }

        TimerSeparator.Visibility = _timerPresets.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnCoordinatorPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ActivityCoordinator.Companion))
        {
            ShowBubble(_viewModel.Coordinator.Companion is not null);
            return;
        }

        if (e.PropertyName != nameof(ActivityCoordinator.Current))
        {
            return;
        }

        Track(_viewModel.Coordinator.Current);
        Morph();
        RevealContent();
        UpdateVideo();
    }

    /// <summary>
    /// Keeps the video window in step: over the island once it has morphed into the video, parked off-screen (still
    /// playing) while something else has the island, stopped when the video closes.
    /// </summary>
    private void UpdateVideo()
    {
        if (_activity is VideoActivity video && !ReferenceEquals(video, _video))
        {
            _video = video;
            video.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(VideoActivity.Source))
                {
                    UpdateVideo();
                }
            };
        }

        if (_video is null)
        {
            return;
        }

        if (_videoWindow is null)
        {
            _videoWindow = new VideoWindow { Owner = this };
            _videoWindow.Show(); // WebView2 starts only once its window exists
            _video.GetPosition = _videoWindow.GetPositionAsync;
            _videoWindow.CloseRequested += (_, _) => _video.CloseCommand.Execute(null);
        }

        _videoPlaceTimer.Stop();
        if (!_video.IsOpen)
        {
            _loadedVideoId = null;
            _videoWindow.Stop();
            ParkVideo();
            return;
        }

        if (_video.Source != _loadedVideoId)
        {
            _loadedVideoId = _video.Source;
            _ = _videoWindow.PlayAsync(_video.Source!, _video.IsYouTube, _video.Start);
        }

        if (ReferenceEquals(_activity, _video))
        {
            // Appear once the pill has (nearly) reached the video's shape.
            _videoPlaceTimer.Interval = _morphDuration.TimeSpan * 0.7;
            _videoPlaceTimer.Start();
        }
        else
        {
            ParkVideo();
        }
    }

    /// <summary>Starts following the island: every frame, so the video stays inside it while it springs or shifts sideways (side bubble).</summary>
    private void PlaceVideo()
    {
        if (_videoWindow is null || !ReferenceEquals(_activity, _video) || _followingIsland)
        {
            return;
        }

        _followingIsland = true;
        _placedVideo = default;
        CompositionTarget.Rendering += FollowIsland;
    }

    private void FollowIsland(object? sender, EventArgs e)
    {
        if (_videoWindow is null || !Island.IsVisible)
        {
            return;
        }

        // The slot is the island's current shape inset by the frame, so it never pokes out mid-animation.
        // PointToScreen includes the Size setting, hover scale and DPI: physical pixels.
        var inset = VideoActivity.VideoRect;
        var topLeft = Island.PointToScreen(inset.TopLeft);
        var bottomRight = Island.PointToScreen(new Point(Island.ActualWidth - inset.Left, Island.ActualHeight - inset.Left));
        var slot = new Int32Rect((int)Math.Round(topLeft.X), (int)Math.Round(topLeft.Y),
            (int)Math.Round(bottomRight.X - topLeft.X), (int)Math.Round(bottomRight.Y - topLeft.Y));
        if (slot.Width <= 0 || slot.Height <= 0 || slot == _placedVideo)
        {
            return;
        }

        _placedVideo = slot;
        var scale = (bottomRight.X - topLeft.X) / (Island.ActualWidth - (2 * inset.Left));
        _videoWindow.Place(slot.X, slot.Y, slot.Width, slot.Height, (int)Math.Round(VideoCornerRadius * scale));
    }

    // Off-screen rather than hidden: a hidden WebView2 tells the page it's in the background.
    private void ParkVideo()
    {
        if (_followingIsland)
        {
            _followingIsland = false;
            CompositionTarget.Rendering -= FollowIsland;
        }

        _videoWindow?.Place(-32000, -32000, (int)VideoActivity.VideoRect.Width, (int)VideoActivity.VideoRect.Height, 0);
    }

    private void Track(IslandActivity? activity)
    {
        if (_activity is not null)
        {
            _activity.PropertyChanged -= OnActivityPropertyChanged;
        }

        _activity = activity;

        if (_activity is not null)
        {
            _activity.PropertyChanged += OnActivityPropertyChanged;
        }
    }

    private void OnActivityPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(IslandActivity.CurrentSize))
        {
            // Raised for anything that might resize (volume, timeline…); re-revealing unchanged content would flicker.
            if (_activity?.CurrentSize == _morphedSize)
            {
                return;
            }

            Morph();
            RevealContent();
        }
        else if (e.PropertyName is nameof(IslandActivity.Tone) or nameof(IslandActivity.GlowColor))
        {
            Morph();
        }
    }

    private void HookOutsideClicks(bool hook)
    {
        if (hook && _mouseHook == IntPtr.Zero)
        {
            _mouseHook = NativeMethods.HookMouse(_mouseProc);
        }
        else if (!hook && _mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookMouse(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private IntPtr OnGlobalMouse(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0 && NativeMethods.IsButtonDown(message))
        {
            // MSLLHOOKSTRUCT starts with the cursor position in physical screen pixels.
            var point = new Point(Marshal.ReadInt32(data), Marshal.ReadInt32(data, 4));
            Dispatcher.BeginInvoke(() =>
            {
                if (_activity?.IsExpanded == true && !Hits(Island, point) && !Hits(Bubble, point))
                {
                    _collapseTimer.Stop();
                    _viewModel.Collapse();
                }
            });
        }

        return NativeMethods.NextMouseHook(code, message, data);
    }

    private static bool Hits(FrameworkElement element, Point screenPoint)
    {
        if (!element.IsVisible || PresentationSource.FromVisual(element) is null)
        {
            return false;
        }

        var p = element.PointFromScreen(screenPoint); // undoes DPI, the Size setting and hover scale
        return p.X >= 0 && p.Y >= 0 && p.X <= element.ActualWidth && p.Y <= element.ActualHeight;
    }

    private void Morph()
    {
        HookOutsideClicks(_activity?.IsExpanded == true);
        var size = _activity?.CurrentSize ?? IdleSize;
        _morphedSize = size;
        var radius = size.Height > PillMaxHeight ? ExpandedCornerRadius : size.Height / 2;

        // While the bubble peeks, the island gives up the width the bubble gains, so the pair keeps its overall width.
        var width = _isPeeking ? Math.Max(size.Height, size.Width - (PeekWidth - ClosedBubbleWidth)) : size.Width;

        // Content is laid out at the final size up front, so it doesn't reflow every frame while the pill morphs.
        ActivityHost.Width = width;
        ActivityHost.Height = size.Height;

        _widthSpring.Target = width;
        _heightSpring.Target = size.Height;
        _radiusSpring.Target = radius;
        if (!_springing)
        {
            _springing = true;
            _lastFrame = null;
            CompositionTarget.Rendering += StepSprings;
        }

        var glow = _activity?.GlowColor ?? _activity?.Tone switch
        {
            ActivityTone.Accent => (Color)FindResource("AccentColor"),
            ActivityTone.Positive => _positiveGlow,
            _ => Colors.Black,
        };
        Glow.BeginAnimation(DropShadowEffect.ColorProperty, new ColorAnimation(glow, _morphDuration));
    }

    /// <summary>
    /// Moves the pill one frame along its springs. Springs keep their speed when the target changes, so a quick
    /// expand/collapse reverses smoothly instead of stopping dead and restarting (as a restarted animation would).
    /// </summary>
    private void StepSprings(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (now == _lastFrame)
        {
            return; // Rendering can fire more than once per frame
        }

        // Clamped so a stalled frame doesn't jump the pill.
        var dt = _lastFrame is { } last ? Math.Clamp((now - last).TotalSeconds, 0, 0.05) : 1 / 60.0;
        _lastFrame = now;

        // Same curve as SpringEase over the morph duration.
        var omega = SpringEase.Frequency / _morphDuration.TimeSpan.TotalSeconds;
        var settled = _widthSpring.Step(dt, omega) & _heightSpring.Step(dt, omega) & _radiusSpring.Step(dt, omega);

        Island.Width = _widthSpring.Value;
        Island.Height = _heightSpring.Value;
        Pill.RadiusX = Pill.RadiusY = _radiusSpring.Value;

        if (settled)
        {
            _springing = false;
            CompositionTarget.Rendering -= StepSprings;
        }
    }

    /// <summary>Grows the side bubble out of the island (or tucks it back) with the island's spring.</summary>
    private void ShowBubble(bool show)
    {
        if (!show)
        {
            SetPeek(false); // give the island its width back
        }

        AnimateTo(Bubble, WidthProperty, show ? ClosedBubbleWidth : 0, _morphDuration, MorphEasing);
        var margin = new Thickness(show ? BubbleGap : 0, TopMargin, 0, 0);
        Bubble.BeginAnimation(MarginProperty, new ThicknessAnimation(margin, _morphDuration) { EasingFunction = MorphEasing }, HandoffBehavior.SnapshotAndReplace);
        Bubble.BeginAnimation(OpacityProperty, new DoubleAnimation(show ? 1 : 0, show ? _morphDuration : HoverDuration), HandoffBehavior.SnapshotAndReplace);
    }

    /// <summary>
    /// A click widens the bubble to show the numbers. With a close delay (Island Settings), it closes on its own and
    /// another click restarts the delay; with "Never", the next click closes it.
    /// </summary>
    private void OnBubbleMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_viewModel.Coordinator.Companion is null)
        {
            return;
        }

        if (!_peekAutoClose)
        {
            SetPeek(!_isPeeking);
            return;
        }

        SetPeek(true);
        _peekTimer.Stop();
        _peekTimer.Start();
    }

    private void SetPeek(bool peek)
    {
        if (!peek)
        {
            _peekTimer.Stop();
        }

        if (peek == _isPeeking)
        {
            return;
        }

        _isPeeking = peek;
        AnimateTo(Bubble, WidthProperty, peek ? PeekWidth : ClosedBubbleWidth, _morphDuration, MorphEasing);
        Morph();
    }

    /// <summary>Exactly as wide as the companion's content (mic, timer, battery and rings all differ).</summary>
    private double PeekWidth => Math.Max(ClosedBubbleWidth, Math.Ceiling(BubbleContent.ActualWidth) + BubblePeekPadding);

    private double ClosedBubbleWidth => _viewModel.Coordinator.Companion?.BubbleWidth ?? BubbleSize;

    /// <summary>Refit the bubble when its content changes width (timer text, app name, icons in use, another companion).</summary>
    private void OnBubbleContentSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!e.WidthChanged || _viewModel.Coordinator.Companion is null)
        {
            return;
        }

        if (_isPeeking)
        {
            AnimateTo(Bubble, WidthProperty, PeekWidth, _morphDuration, MorphEasing);
            Morph();
        }
        else
        {
            AnimateTo(Bubble, WidthProperty, ClosedBubbleWidth, _morphDuration, MorphEasing);
        }
    }

    /// <summary>
    /// The template swaps compact/expanded content instantly; hide the new content and let it
    /// fade and scale in once the pill has mostly reached its new shape.
    /// </summary>
    private void RevealContent()
    {
        ActivityHost.BeginAnimation(OpacityProperty, Delayed(0, 1));
        ContentScale.BeginAnimation(ScaleTransform.ScaleXProperty, Delayed(ContentStartScale, 1));
        ContentScale.BeginAnimation(ScaleTransform.ScaleYProperty, Delayed(ContentStartScale, 1));
    }

    /// <summary>Holds <paramref name="from"/> for the content delay, then eases to <paramref name="to"/>.</summary>
    private DoubleAnimationUsingKeyFrames Delayed(double from, double to) => new()
    {
        KeyFrames =
        {
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(TimeSpan.Zero)),
            new DiscreteDoubleKeyFrame(from, KeyTime.FromTimeSpan(_contentDelay)),
            new EasingDoubleKeyFrame(to, KeyTime.FromTimeSpan(_contentDelay + _contentFade), ContentEasing),
        },
    };

    // File shelf. Dropping adds files; pressing on a shelf file (or the closed shelf / its bubble, for every file) and
    // moving starts a drag back out. Copy only, so a drop never moves the user's original away.
    private ShelfFile[]? _pressedFiles;
    private Point _pressPoint;
    private bool _draggingOut;

    private static bool HasFiles(DragEventArgs e) => e.Data.GetDataPresent(DataFormats.FileDrop);

    private readonly DropHintActivity _dropHint = new();

    private void OnShelfDragEnter(object sender, DragEventArgs e)
    {
        if (_draggingOut || !HasFiles(e) || ReferenceEquals(_activity, _dropHint))
        {
            return;
        }

        // The island springs open into a drop target; held until the drop or the drag leaves.
        _viewModel.Coordinator.Flash(_dropHint, TimeSpan.FromMinutes(5));
        KeepDropHint();
    }

    // Not DragLeave: it also fires when the pointer crosses into a child element, and on Esc it reports a point
    // still inside. Windows sends DragOver every few ms while files hover, so the hint ends once those stop.
    private DispatcherTimer? _dropHintWatchdog;

    private void KeepDropHint()
    {
        if (_dropHintWatchdog is null)
        {
            _dropHintWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _dropHintWatchdog.Tick += (_, _) => EndDropHint();
        }

        _dropHintWatchdog.Stop();
        _dropHintWatchdog.Start();
    }

    private void EndDropHint()
    {
        _dropHintWatchdog?.Stop();
        ScaleTo(1);
        _viewModel.Coordinator.Hide(_dropHint);
    }

    private void OnShelfDragOver(object sender, DragEventArgs e)
    {
        e.Effects = !_draggingOut && HasFiles(e) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        if (ReferenceEquals(_activity, _dropHint))
        {
            KeepDropHint();
        }
    }

    private void OnShelfDrop(object sender, DragEventArgs e)
    {
        EndDropHint();
        if (!_draggingOut && e.Data.GetData(DataFormats.FileDrop) is string[] paths)
        {
            _viewModel.Shelf.Add(paths);
        }

        e.Handled = true;
    }

    private void OnShelfPressed(object sender, MouseButtonEventArgs e)
    {
        _pressedFiles = null;
        if (e.OriginalSource is not FrameworkElement source || IsInsideButton(source, (DependencyObject)sender))
        {
            return; // the remove / clear buttons
        }

        _pressedFiles = source.DataContext switch
        {
            ShelfFile file => [file],
            ShelfActivity { IsExpanded: false, Files.Count: > 0 } shelf => [.. shelf.Files],
            _ => null,
        };
        _pressPoint = e.GetPosition(this);
    }

    private void OnShelfMouseMove(object sender, MouseEventArgs e)
    {
        if (_pressedFiles is not { } files || e.LeftButton != MouseButtonState.Pressed)
        {
            return;
        }

        var moved = e.GetPosition(this) - _pressPoint;
        if (Math.Abs(moved.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(moved.Y) < SystemParameters.MinimumVerticalDragDistance)
        {
            return;
        }

        _pressedFiles = null;
        _draggingOut = true;
        try
        {
            var data = new DataObject(DataFormats.FileDrop, files.Select(f => f.Path).ToArray());
            if (DragDrop.DoDragDrop((DependencyObject)sender, data, DragDropEffects.Copy) != DragDropEffects.None)
            {
                _viewModel.Shelf.Remove(files); // delivered: off the shelf
            }
        }
        finally
        {
            _draggingOut = false;
        }
    }

    private void OnShelfReleased(object sender, MouseButtonEventArgs e)
    {
        // A click (no drag) on a file in the open shelf opens it instead of collapsing the island.
        if (_pressedFiles is [var file] && _activity is ShelfActivity { IsExpanded: true })
        {
            ShelfActivity.Open(file);
            e.Handled = true;
        }

        _pressedFiles = null;
    }

    private static bool IsInsideButton(DependencyObject element, DependencyObject root)
    {
        for (var node = element; node is not null && !ReferenceEquals(node, root); node = VisualTreeHelper.GetParent(node))
        {
            if (node is System.Windows.Controls.Primitives.ButtonBase)
            {
                return true;
            }
        }

        return false;
    }

    private void OnIslandMouseEnter(object sender, MouseEventArgs e)
    {
        _collapseTimer.Stop();
        if (_activity is not VideoActivity) // the video window can't scale with the island
        {
            ScaleTo(HoverScaleFactor);
        }
    }

    private void OnIslandMouseLeave(object sender, MouseEventArgs e)
    {
        ScaleTo(1);
        if (_autoCollapse && _activity?.IsExpanded == true)
        {
            _collapseTimer.Start();
        }
    }

    private void OnIslandMouseLeftButtonUp(object sender, MouseButtonEventArgs e) =>
        _viewModel.ToggleExpandCommand.Execute(null);

    private void OnCollapseTimerTick(object? sender, EventArgs e)
    {
        _collapseTimer.Stop();
        _viewModel.Collapse();
    }

    private void ScaleTo(double scale)
    {
        AnimateTo(HoverScale, ScaleTransform.ScaleXProperty, scale, HoverDuration, HoverEasing);
        AnimateTo(HoverScale, ScaleTransform.ScaleYProperty, scale, HoverDuration, HoverEasing);
    }

    private static void AnimateTo(IAnimatable target, DependencyProperty property, double to, Duration duration, IEasingFunction easing) =>
        target.BeginAnimation(property, new DoubleAnimation(to, duration) { EasingFunction = easing }, HandoffBehavior.SnapshotAndReplace);

    private static IEasingFunction Frozen(EasingFunctionBase easing)
    {
        easing.Freeze();
        return easing;
    }

    /// <summary>
    /// Critically damped spring: the fastest settle with no overshoot or bounce, normalised to settle by t = 1.
    /// </summary>
    private sealed class SpringEase : EasingFunctionBase
    {
        public const double Frequency = 8.0; // e^-8 * 9 ≈ 0.3% left at t = 1

        public SpringEase() => EasingMode = EasingMode.EaseIn; // EaseIn = use the curve as written

        protected override double EaseInCore(double normalizedTime)
        {
            if (normalizedTime >= 1)
            {
                return 1;
            }

            var wt = Frequency * normalizedTime;
            return 1 - (Math.Exp(-wt) * (1 + wt));
        }

        protected override Freezable CreateInstanceCore() => new SpringEase();
    }

    /// <summary>A critically damped spring (the SpringEase curve), stepped exactly so any frame rate gives the same motion.</summary>
    private sealed class Spring(double value)
    {
        public double Value { get; private set; } = value;

        public double Target { get; set; } = value;

        private double _velocity;

        /// <returns>True once it has come to rest on the target.</returns>
        public bool Step(double dt, double omega)
        {
            var offset = Value - Target;
            var a = _velocity + (omega * offset);
            var decay = Math.Exp(-omega * dt);
            offset = (offset + (a * dt)) * decay;
            _velocity = (_velocity - (omega * a * dt)) * decay;

            if (Math.Abs(offset) < 0.05 && Math.Abs(_velocity) < 0.5)
            {
                offset = 0;
                _velocity = 0;
            }

            Value = Target + offset;
            return offset == 0;
        }
    }
}
