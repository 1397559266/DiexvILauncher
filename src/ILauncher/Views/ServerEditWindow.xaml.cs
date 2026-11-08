using System.Windows;
using ILauncher.Core.Services;

namespace ILauncher.Views;

public partial class ServerEditWindow : Window
{
    public ServerEditWindow(ServerEntry? existing = null)
    {
        InitializeComponent();
        DialogTitle = existing == null ? "添加服务器" : "编辑服务器";
        DataContext = this;
        NameBox.Text = existing?.Name ?? "";
        AddressBox.Text = existing?.Ip ?? "";
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(NameBox.Text))
            {
                AddressBox.Focus();
            }
            else
            {
                NameBox.Focus();
            }
        };
    }

    public string DialogTitle { get; }
    public string ServerName { get; private set; } = "";
    public string ServerAddress { get; private set; } = "";

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(address))
        {
            MessageBox.Show("请输入服务器地址。", "服务器", MessageBoxButton.OK, MessageBoxImage.Information);
            AddressBox.Focus();
            return;
        }

        ServerAddress = address;
        ServerName = string.IsNullOrWhiteSpace(NameBox.Text) ? address : NameBox.Text.Trim();
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
