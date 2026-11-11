using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using ILauncher.ViewModels;

namespace ILauncher;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        StateChanged += (_, _) => UpdateMaximizeIcon();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => QueueNavUpdate(false);
        NavTabs.SizeChanged += (_, _) => QueueNavUpdate(false);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is INotifyPropertyChanged oldViewModel)
        {
            oldViewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is INotifyPropertyChanged newViewModel)
        {
            newViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        QueueNavUpdate(false);
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(MainViewModel.IsHomeOrInstance):
            case nameof(MainViewModel.IsDownloadPage):
            case nameof(MainViewModel.IsAccountPage):
            case nameof(MainViewModel.IsSettingsPage):
                QueueNavUpdate(true);
                break;
        }
    }

    private void QueueNavUpdate(bool animate)
    {
        // Tag bindings settle on the DataBind pass, layout on the Render pass;
        // Loaded priority runs after both, so the active button has a real size.
        Dispatcher.BeginInvoke(new Action(() => UpdateNavIndicator(animate)), DispatcherPriority.Loaded);
    }

    private void UpdateNavIndicator(bool animate)
    {
        Button? active = null;
        foreach (var child in NavTabs.Children)
        {
            if (child is Button button && button.Tag is true)
            {
                active = button;
                break;
            }
        }

        if (active is null || active.ActualWidth <= 0)
        {
            NavIndicator.Opacity = 0;
            return;
        }

        NavIndicator.Opacity = 1;
        var offset = active.TransformToAncestor(NavHost).Transform(new Point(0, 0));

        if (!animate)
        {
            NavIndicatorShift.BeginAnimation(TranslateTransform.XProperty, null);
            NavIndicator.BeginAnimation(WidthProperty, null);
            NavIndicatorShift.X = offset.X;
            NavIndicator.Width = active.ActualWidth;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        NavIndicatorShift.BeginAnimation(TranslateTransform.XProperty,
            new DoubleAnimation(offset.X, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
        NavIndicator.BeginAnimation(WidthProperty,
            new DoubleAnimation(active.ActualWidth, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (FindAncestor<ButtonBase>(e.OriginalSource as DependencyObject) != null)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        try
        {
            DragMove();
        }
        catch
        {
            // ignored
        }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        => ToggleMaximize();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => Close();

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void UpdateMaximizeIcon()
    {
        if (MaximizeIcon == null)
        {
            return;
        }

        MaximizeIcon.Data = (Geometry)FindResource(
            WindowState == WindowState.Maximized ? "IconRestore" : "IconMaximize");
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
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
