using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ILauncher.Controls;

/// <summary>
/// A grey placeholder block with a slow highlight sweeping across it, used to
/// sketch the shape of content that is still loading.
/// </summary>
public sealed class Skeleton : Control
{
    private static readonly Brush DefaultBase = Freeze(new SolidColorBrush(Color.FromRgb(0xE8, 0xEF, 0xF8)));

    public static readonly DependencyProperty BaseBrushProperty = DependencyProperty.Register(
        nameof(BaseBrush), typeof(Brush), typeof(Skeleton),
        new FrameworkPropertyMetadata(DefaultBase, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CornerRadiusProperty = DependencyProperty.Register(
        nameof(CornerRadius), typeof(CornerRadius), typeof(Skeleton),
        new FrameworkPropertyMetadata(new CornerRadius(6)));

    static Skeleton()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(Skeleton), new FrameworkPropertyMetadata(typeof(Skeleton)));
    }

    public Brush BaseBrush
    {
        get => (Brush)GetValue(BaseBrushProperty);
        set => SetValue(BaseBrushProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private static Brush Freeze(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}
