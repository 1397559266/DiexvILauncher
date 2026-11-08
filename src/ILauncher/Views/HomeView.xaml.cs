using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using ILauncher.Services;
using ILauncher.ViewModels;

namespace ILauncher.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        InstanceIconService.Changed -= OnIconChanged;
        InstanceIconService.Changed += OnIconChanged;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        InstanceIconService.Changed -= OnIconChanged;
    }

    private void OnIconChanged(object? sender, string instanceId)
    {
        Dispatcher.Invoke(() => VersionList.Items.Refresh());
    }

    private void VersionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel { SelectedInstance: { } instance } viewModel)
        {
            viewModel.OpenInstance(instance);
        }
    }

    private void HomeScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || sender is not ScrollViewer scrollViewer)
        {
            return;
        }

        var lines = SystemParameters.WheelScrollLines;
        var amount = lines > 0 ? lines * 32d : 48d;
        scrollViewer.ScrollToVerticalOffset(scrollViewer.VerticalOffset - Math.Sign(e.Delta) * amount);
        e.Handled = true;
    }
}
