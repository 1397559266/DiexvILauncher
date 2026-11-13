using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using ILauncher.ViewModels;

namespace ILauncher.Views;

public partial class ModsView : UserControl
{
    public ModsView()
    {
        InitializeComponent();
    }

    /// <summary>单击整行即切换该模组的选中状态，可以连续点击选中多个。</summary>
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not DependencyObject source)
        {
            return;
        }

        // 点在开关或「更新」按钮上时交给按钮自己处理，不改变选中状态。
        if (FindAncestor<ButtonBase>(source) != null)
        {
            return;
        }

        if (FindAncestor<ListBoxItem>(source)?.DataContext is not ModItemViewModel mod)
        {
            return;
        }

        mod.IsSelected = !mod.IsSelected;
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        var jars = GetDroppedJars(e);
        e.Effects = jars.Count > 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        DropOverlay.Visibility = jars.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnDragLeave(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        DropOverlay.Visibility = Visibility.Collapsed;
        var jars = GetDroppedJars(e);
        if (jars.Count == 0 || DataContext is not ModsViewModel viewModel)
        {
            return;
        }

        e.Handled = true;
        await viewModel.InstallDroppedAsync(jars);
    }

    private static List<string> GetDroppedJars(DragEventArgs e)
    {
        var result = new List<string>();
        if (!e.Data.GetDataPresent(DataFormats.FileDrop) ||
            e.Data.GetData(DataFormats.FileDrop) is not string[] paths)
        {
            return result;
        }

        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                result.AddRange(Directory.EnumerateFiles(path, "*.jar", SearchOption.TopDirectoryOnly));
            }
            else if (path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                result.Add(path);
            }
        }

        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
