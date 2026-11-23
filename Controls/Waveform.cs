using System.Diagnostics;
using System.Windows;
using System.Windows.Media;

namespace IslandApp.Controls;

/// <summary>
/// Lightweight equaliser that follows the real output level (<see cref="LevelSource"/>). Draws into a retained <see cref="DrawingGroup"/> so frames
/// update without layout passes, is throttled to 30 fps, and only animates while active and visible.
/// </summary>
public sealed class Waveform : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(Waveform),
        new FrameworkPropertyMetadata(false, (d, _) => ((Waveform)d).UpdateSubscription()));

    public static readonly DependencyProperty BarCountProperty = DependencyProperty.Register(
        nameof(BarCount), typeof(int), typeof(Waveform),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(Waveform),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    private const double RestAmplitude = 0.3;
    private const double MinAmplitude = 0.15;
    private const double Attack = 0.7;   // fraction of the gap closed per frame when rising
    private const double Release = 0.2;  // ...and when falling, so beats pop and decay smoothly
    private const double PeakDecay = 0.995; // auto-gain memory: halves in ~4.5 s at 30 fps
    private const double PeakFloor = 0.02;  // don't amplify near-silence into full bars

    private static readonly TimeSpan FrameInterval = TimeSpan.FromSeconds(1d / 30);

    private readonly DrawingGroup _drawing = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private TimeSpan _lastFrame;
    private bool _isSubscribed;
    private double[] _bars = [];
    private double _runningPeak = PeakFloor;

    public Waveform() => IsVisibleChanged += (_, _) => UpdateSubscription();

    /// <summary>Output peak (0..1), polled once per frame. Null falls back to a synthetic animation.</summary>
    public static Func<float>? LevelSource { get; set; }

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    public int BarCount
    {
        get => (int)GetValue(BarCountProperty);
        set => SetValue(BarCountProperty, value);
    }

    public Brush Brush
    {
        get => (Brush)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        Redraw();
        drawingContext.DrawDrawing(_drawing);
    }

    private void UpdateSubscription()
    {
        var shouldAnimate = IsActive && IsVisible;
        if (shouldAnimate == _isSubscribed)
        {
            return;
        }

        _isSubscribed = shouldAnimate;
        if (shouldAnimate)
        {
            CompositionTarget.Rendering += OnRendering;
        }
        else
        {
            CompositionTarget.Rendering -= OnRendering;
            Redraw();
        }
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        if (now - _lastFrame < FrameInterval)
        {
            return;
        }

        _lastFrame = now;
        Redraw();
    }

    private void Redraw()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var count = Math.Max(1, BarCount);
        if (width <= 0 || height <= 0)
        {
            return;
        }

        var barWidth = Math.Min(3.5, width / (count * 1.8));
        var gap = count > 1 ? (width - (count * barWidth)) / (count - 1) : 0;
        var t = _clock.Elapsed.TotalSeconds;
        var radius = barWidth / 2;
        var level = IsActive ? NormalizedLevel() : null;
        if (_bars.Length != count)
        {
            _bars = new double[count];
        }

        using var context = _drawing.Open();
        for (var i = 0; i < count; i++)
        {
            double amplitude;
            if (!IsActive)
            {
                amplitude = RestAmplitude;
            }
            else if (level is { } l)
            {
                // One level drives every bar; a slow per-bar wobble keeps them from moving in lockstep.
                var target = l * (0.6 + (0.4 * (0.5 + (0.5 * Math.Sin((t * (3.1 + (i * 1.7))) + (i * 2.3))))));
                _bars[i] += (target - _bars[i]) * (target > _bars[i] ? Attack : Release);
                amplitude = MinAmplitude + ((1 - MinAmplitude) * _bars[i]);
            }
            else
            {
                amplitude = 0.3 + (0.7 * (0.5 + (0.5 * Math.Sin((t * (5.1 + (i * 1.35))) + (i * 1.7)))));
            }

            var barHeight = Math.Max(barWidth, height * amplitude);
            var rect = new Rect(i * (barWidth + gap), (height - barHeight) / 2, barWidth, barHeight);
            context.DrawRoundedRectangle(Brush, null, rect, radius, radius);
        }
    }

    /// <summary>Peak scaled against the recent loudest peak, so bars use their full height at any volume.</summary>
    private double? NormalizedLevel()
    {
        if (LevelSource is not { } source)
        {
            return null;
        }

        var peak = Math.Clamp(source(), 0, 1);
        _runningPeak = Math.Max(peak, Math.Max(PeakFloor, _runningPeak * PeakDecay));
        return peak / _runningPeak;
    }
}
