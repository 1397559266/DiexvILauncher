using System.Collections.ObjectModel;
using System.Windows;
using ILauncher.Core.Services;

namespace ILauncher.Views;

public partial class ModVersionWindow : Window
{
    public ModVersionWindow(ModrinthProject project, IReadOnlyList<ModrinthVersion> versions)
    {
        Project = project;
        Versions = new ObservableCollection<ModrinthVersion>(versions);
        SelectedVersion = Versions.FirstOrDefault();
        InitializeComponent();
        DataContext = this;
    }

    public ModrinthProject Project { get; }
    public ObservableCollection<ModrinthVersion> Versions { get; }
    public ModrinthVersion? SelectedVersion { get; set; }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion == null)
        {
            MessageBox.Show("请选择一个文件版本。", "选择版本", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        DialogResult = true;
    }
}
