using System.Windows;
using System.Windows.Media;

namespace IslandApp.Controls;

/// <summary>Circular progress: a track ring and an arc from 12 o'clock, clockwise, for <see cref="Value"/> (0..1).</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(RingGauge),
        new FrameworkPropertyMetadata(3d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BrushProperty = DependencyProperty.Register(
        nameof(Brush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.White, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(RingGauge),
        new FrameworkPropertyMetadata(Brushes.Gray, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    public Brush Brush
    {
        get => (Brush)GetValue(BrushProperty);
        set => SetValue(BrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var size = Math.Min(ActualWidth, ActualHeight);
        var radius = (size - Thickness) / 2;
        if (radius <= 0)
        {
            return;
        }

        var center = new Point(ActualWidth / 2, ActualHeight / 2);
        drawingContext.DrawEllipse(null, new Pen(TrackBrush, Thickness), center, radius, radius);

        var value = Math.Clamp(Value, 0, 1);
        var pen = new Pen(Brush, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        if (value >= 0.9999)
        {
            drawingContext.DrawEllipse(null, pen, center, radius, radius);
            return;
        }

        if (value <= 0)
        {
            return;
        }

        var angle = value * 2 * Math.PI;
        var start = new Point(center.X, center.Y - radius);
        var end = new Point(center.X + (radius * Math.Sin(angle)), center.Y - (radius * Math.Cos(angle)));
        var arc = new StreamGeometry();
        using (var context = arc.Open())
        {
            context.BeginFigure(start, isFilled: false, isClosed: false);
            context.ArcTo(end, new Size(radius, radius), 0, isLargeArc: value > 0.5, SweepDirection.Clockwise, isStroked: true, isSmoothJoin: false);
        }

        arc.Freeze();
        drawingContext.DrawGeometry(null, pen, arc);
    }
}
