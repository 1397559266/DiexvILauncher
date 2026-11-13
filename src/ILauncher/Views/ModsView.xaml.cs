using System.IO;
using System.Windows;
using System.Windows.Controls;
using ILauncher.ViewModels;

namespace ILauncher.Views;

public partial class ModsView : UserControl
{
    public ModsView()
    {
        InitializeComponent();
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
