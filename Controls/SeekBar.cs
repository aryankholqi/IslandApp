using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace IslandApp.Controls;

/// <summary>
/// Thin progress bar that can be clicked or dragged to seek. Draws in OnRender so progress updates
/// don't trigger layout. Executes <see cref="Command"/> with the target fraction (0–1) on release.
/// </summary>
public sealed class SeekBar : FrameworkElement
{
    public static readonly DependencyProperty ProgressProperty = DependencyProperty.Register(
        nameof(Progress), typeof(double), typeof(SeekBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackProperty = DependencyProperty.Register(
        nameof(Track), typeof(Brush), typeof(SeekBar),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CommandProperty = DependencyProperty.Register(
        nameof(Command), typeof(ICommand), typeof(SeekBar));

    private const double BarHeight = 4;
    private const double ActiveBarHeight = 6;

    private bool _isDragging;
    private double _dragFraction;

    static SeekBar() => IsEnabledProperty.OverrideMetadata(typeof(SeekBar),
        new UIPropertyMetadata(true, (d, _) => ((SeekBar)d).InvalidateVisual()));

    public double Progress
    {
        get => (double)GetValue(ProgressProperty);
        set => SetValue(ProgressProperty, value);
    }

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public Brush Track
    {
        get => (Brush)GetValue(TrackProperty);
        set => SetValue(TrackProperty, value);
    }

    public ICommand? Command
    {
        get => (ICommand?)GetValue(CommandProperty);
        set => SetValue(CommandProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        var active = IsEnabled && (_isDragging || IsMouseOver);
        var height = active ? ActiveBarHeight : BarHeight;
        var top = (ActualHeight - height) / 2;
        var radius = height / 2;
        var fraction = Math.Clamp(_isDragging ? _dragFraction : Progress, 0, 1);

        // Transparent background makes the full control height hit-testable, not just the thin bar.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize));
        dc.DrawRoundedRectangle(Track, null, new Rect(0, top, width, height), radius, radius);
        if (fraction > 0)
        {
            dc.DrawRoundedRectangle(Fill, null, new Rect(0, top, Math.Max(height, width * fraction), height), radius, radius);
        }
    }

    protected override void OnMouseEnter(MouseEventArgs e) => InvalidateVisual();

    protected override void OnMouseLeave(MouseEventArgs e) => InvalidateVisual();

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (CaptureMouse())
        {
            _isDragging = true;
            UpdateDrag(e);
        }

        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_isDragging)
        {
            UpdateDrag(e);
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        // Handled so the island's click-to-collapse doesn't fire after a seek.
        e.Handled = true;
        if (!_isDragging)
        {
            return;
        }

        UpdateDrag(e);
        var fraction = _dragFraction;
        ReleaseMouseCapture();
        if (Command?.CanExecute(fraction) == true)
        {
            Command.Execute(fraction);
        }
    }

    protected override void OnLostMouseCapture(MouseEventArgs e)
    {
        _isDragging = false;
        InvalidateVisual();
    }

    private void UpdateDrag(MouseEventArgs e)
    {
        _dragFraction = ActualWidth > 0 ? Math.Clamp(e.GetPosition(this).X / ActualWidth, 0, 1) : 0;
        InvalidateVisual();
    }
}
