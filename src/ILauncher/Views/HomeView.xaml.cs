using System.Windows.Controls;
using System.Windows.Input;
using ILauncher.ViewModels;

namespace ILauncher.Views;

public partial class HomeView : UserControl
{
    public HomeView()
    {
        InitializeComponent();
    }

    private void VersionList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel { SelectedInstance: { } instance } viewModel)
        {
            viewModel.OpenInstance(instance);
        }
    }
}
