using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ILauncher.Controls;

/// <summary>
/// Fades a list row in from below, offset by its index in the owning
/// <see cref="ItemsControl"/>, so a freshly bound list cascades instead of
/// popping in all at once. Works on the item container or on the template root.
/// </summary>
public static class ListAnimator
{
    private const int MaxStaggeredItems = 12;
    private const double StaggerMilliseconds = 26;

    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(ListAnimator),
        new PropertyMetadata(false, OnIsEnabledChanged));

    public static void SetIsEnabled(DependencyObject element, bool value)
        => element.SetValue(IsEnabledProperty, value);

    public static bool GetIsEnabled(DependencyObject element)
        => (bool)element.GetValue(IsEnabledProperty);

    private static void OnIsEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;
        if (e.NewValue is true)
        {
            element.Loaded += OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element)
        {
            return;
        }

        element.Loaded -= OnLoaded;
        Animate(element);
    }

    private static void Animate(FrameworkElement element)
    {
        var index = ResolveIndex(element);
        var delay = TimeSpan.FromMilliseconds(Math.Min(index, MaxStaggeredItems) * StaggerMilliseconds);
        var duration = TimeSpan.FromMilliseconds(240);
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        // Keyframes keep the element pinned at the start value during the delay,
        // and the base value stays "fully visible" so a skipped animation can
        // never leave a row invisible.
        var fade = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay)));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(delay + duration))
        {
            EasingFunction = ease
        });

        var slide = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.HoldEnd };
        slide.KeyFrames.Add(new LinearDoubleKeyFrame(10, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        slide.KeyFrames.Add(new LinearDoubleKeyFrame(10, KeyTime.FromTimeSpan(delay)));
        slide.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(delay + duration))
        {
            EasingFunction = ease
        });

        var transform = new TranslateTransform();
        element.RenderTransform = transform;
        element.BeginAnimation(UIElement.OpacityProperty, fade);
        transform.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private static int ResolveIndex(FrameworkElement element)
    {
        var owner = ItemsControl.ItemsControlFromItemContainer(element);
        if (owner is null)
        {
            var ancestor = FindAncestor<ItemsControl>(element);
            if (ancestor?.Items is null || element.DataContext is null)
            {
                return 0;
            }

            var index = ancestor.Items.IndexOf(element.DataContext);
            return index < 0 ? 0 : index;
        }

        var containerIndex = owner.ItemContainerGenerator.IndexFromContainer(element);
        return containerIndex < 0 ? 0 : containerIndex;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current is not null)
        {
            if (current is T match)
            {
                return match;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return null;
    }
}
