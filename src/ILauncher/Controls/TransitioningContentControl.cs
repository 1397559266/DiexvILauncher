using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace ILauncher.Controls;

/// <summary>
/// A <see cref="ContentControl"/> that fades and slides each new page into place.
/// Only the incoming page is animated, so switching views stays cheap: two
/// transform/opacity animations on one element instead of a full visual-tree
/// snapshot of the outgoing page.
/// </summary>
public sealed class TransitioningContentControl : ContentControl
{
    public static readonly DependencyProperty IsAnimatedProperty = DependencyProperty.Register(
        nameof(IsAnimated), typeof(bool), typeof(TransitioningContentControl),
        new FrameworkPropertyMetadata(true));

    private readonly TranslateTransform _slide = new(0, 0);
    private bool _pendingEnter;

    public TransitioningContentControl()
    {
        RenderTransform = _slide;
        IsTabStop = false;
        Focusable = false;
        Loaded += OnLoaded;
    }

    public bool IsAnimated
    {
        get => (bool)GetValue(IsAnimatedProperty);
        set => SetValue(IsAnimatedProperty, value);
    }

    protected override void OnContentChanged(object oldContent, object newContent)
    {
        base.OnContentChanged(oldContent, newContent);

        if (newContent is null)
        {
            return;
        }

        if (!IsAnimated)
        {
            FinishEnter();
            return;
        }

        if (!IsLoaded)
        {
            _pendingEnter = true;
            return;
        }

        BeginEnter();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_pendingEnter)
        {
            BeginEnter();
        }
    }

    private void BeginEnter()
    {
        _pendingEnter = false;

        var fade = new DoubleAnimation(0d, 1d, TimeSpan.FromMilliseconds(190))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        var slide = new DoubleAnimation(22d, 0d, TimeSpan.FromMilliseconds(280))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        BeginAnimation(OpacityProperty, fade);
        _slide.BeginAnimation(TranslateTransform.XProperty, slide);
    }

    private void FinishEnter()
    {
        _pendingEnter = false;
        BeginAnimation(OpacityProperty, null);
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        Opacity = 1;
        _slide.X = 0;
    }
}
