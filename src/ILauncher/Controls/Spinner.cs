using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ILauncher.Controls;

/// <summary>
/// Lightweight indeterminate spinner: a thin track ring with a rotating head arc.
/// Rendered with vector primitives and a single rotating transform, so it stays
/// cheap even when several instances are visible at once.
/// </summary>
public sealed class Spinner : Control
{
    private static readonly Brush DefaultHead = Freeze(new SolidColorBrush(Color.FromRgb(0x13, 0x70, 0xF3)));
    private static readonly Brush DefaultTrack = Freeze(new SolidColorBrush(Color.FromArgb(0x33, 0x13, 0x70, 0xF3)));

    public static readonly DependencyProperty HeadBrushProperty = DependencyProperty.Register(
        nameof(HeadBrush), typeof(Brush), typeof(Spinner),
        new FrameworkPropertyMetadata(DefaultHead));

    public static readonly DependencyProperty TrackBrushProperty = DependencyProperty.Register(
        nameof(TrackBrush), typeof(Brush), typeof(Spinner),
        new FrameworkPropertyMetadata(DefaultTrack));

    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(
        nameof(Thickness), typeof(double), typeof(Spinner),
        new FrameworkPropertyMetadata(2.2d));

    static Spinner()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Spinner), new FrameworkPropertyMetadata(typeof(Spinner)));
    }

    public Brush HeadBrush
    {
        get => (Brush)GetValue(HeadBrushProperty);
        set => SetValue(HeadBrushProperty, value);
    }

    public Brush TrackBrush
    {
        get => (Brush)GetValue(TrackBrushProperty);
        set => SetValue(TrackBrushProperty, value);
    }

    public double Thickness
    {
        get => (double)GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
