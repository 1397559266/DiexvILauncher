using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using ILauncher.ViewModels;

namespace ILauncher.Views;

public partial class LaunchLogView : UserControl
{
    private LaunchLogViewModel? _viewModel;
    private DateTime _lastProgrammaticScroll = DateTime.MinValue;
    private bool _loaded;

    public LaunchLogView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        DataContextChanged += OnDataContextChanged;
        LogList.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        Attach(DataContext as LaunchLogViewModel);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        Detach();
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (_loaded)
        {
            Attach(e.NewValue as LaunchLogViewModel);
        }
    }

    private void Attach(LaunchLogViewModel? viewModel)
    {
        Detach();
        _viewModel = viewModel;
        if (_viewModel == null)
        {
            return;
        }

        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += OnItemsChanged;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        _viewModel.ScrollToEndRequested += OnScrollToEndRequested;
    }

    private void Detach()
    {
        if (_viewModel != null)
        {
            ((INotifyCollectionChanged)LogList.Items).CollectionChanged -= OnItemsChanged;
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _viewModel.ScrollToEndRequested -= OnScrollToEndRequested;
            _viewModel = null;
        }
    }

    private void OnItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (_viewModel?.AutoScroll != true)
        {
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToEnd));
    }

    private void OnScrollToEndRequested(object? sender, EventArgs e)
    {
        if (_viewModel?.AutoScroll == true)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToEnd));
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(LaunchLogViewModel.AutoScroll) && _viewModel?.AutoScroll == true)
        {
            Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(ScrollToEnd));
        }
    }

    private void ScrollToEnd()
    {
        if (LogList.Items.Count == 0)
        {
            return;
        }

        _lastProgrammaticScroll = DateTime.Now;
        var last = LogList.Items[LogList.Items.Count - 1];
        LogList.ScrollIntoView(last);
    }

    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_viewModel == null)
        {
            return;
        }

        if (DateTime.Now - _lastProgrammaticScroll < TimeSpan.FromMilliseconds(220))
        {
            return;
        }

        if (e.ExtentHeightChange != 0)
        {
            if (_viewModel.AutoScroll)
            {
                ScrollToEnd();
            }

            return;
        }

        var scrollViewer = FindScrollViewer(LogList);
        if (scrollViewer == null)
        {
            return;
        }

        var atBottom = scrollViewer.VerticalOffset >= scrollViewer.ScrollableHeight - 3;
        if (!atBottom && _viewModel.AutoScroll && scrollViewer.ScrollableHeight > 0)
        {
            _viewModel.AutoScroll = false;
        }
        else if (atBottom && !_viewModel.AutoScroll && scrollViewer.ScrollableHeight > 0)
        {
            _viewModel.AutoScroll = true;
        }
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer viewer)
        {
            return viewer;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var result = FindScrollViewer(VisualTreeHelper.GetChild(root, i));
            if (result != null)
            {
                return result;
            }
        }

        return null;
    }
}
