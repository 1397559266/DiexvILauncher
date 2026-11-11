using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Navigation;
using ILauncher.Core.Services;
using ILauncher.Localization;

namespace ILauncher.Views;

public partial class MicrosoftLoginWindow : Window
{
    private readonly SettingsService _settings;
    private readonly MicrosoftAuthService _auth = new();
    private CancellationTokenSource? _cts;
    private string? _verificationUri;

    public MicrosoftLoginWindow(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        Loaded += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(_settings.Settings.MicrosoftClientId))
            {
                ShowSetup();
            }
            else
            {
                ShowLogin();
                _ = StartAsync();
            }
        };
        Closed += (_, _) => _cts?.Cancel();
    }

    public MicrosoftAccount? Account { get; private set; }

    private void ShowSetup()
    {
        SetupPanel.Visibility = Visibility.Visible;
        LoginPanel.Visibility = Visibility.Collapsed;
        ContinueButton.Visibility = Visibility.Visible;
        ContinueButton.Content = "保存并登录";
    }

    private void ShowLogin()
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        ContinueButton.Visibility = Visibility.Collapsed;
    }

    private async Task StartAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        LoginSpinner.Visibility = Visibility.Visible;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, "BrushAccent");
        try
        {
            var clientId = _settings.Settings.MicrosoftClientId.Trim();
            var code = await _auth.StartDeviceCodeAsync(clientId, _cts.Token);
            CodeText.Text = code.UserCode;
            MessageText.Text = code.Message;
            _verificationUri = code.VerificationUri;
            StatusText.Text = "正在打开浏览器，请完成登录…";
            try
            {
                Process.Start(new ProcessStartInfo(code.VerificationUri) { UseShellExecute = true });
            }
            catch
            {
                // The link is still available in the window.
            }

            var account = await _auth.CompleteAsync(clientId, code,
                status => Dispatcher.Invoke(() => StatusText.Text = status), _cts.Token);
            Account = account;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            LoginSpinner.Visibility = Visibility.Collapsed;
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "BrushAccent");
            StatusText.Text = "登录已取消。";
        }
        catch (Exception ex)
        {
            LoginSpinner.Visibility = Visibility.Collapsed;
            StatusText.SetResourceReference(TextBlock.ForegroundProperty, "BrushDanger");
            StatusText.Text = ex.Message.Contains("失败", StringComparison.Ordinal)
                ? ex.Message
                : "登录失败：" + ex.Message;
        }
    }

    private void Continue_Click(object sender, RoutedEventArgs e)
    {
        var clientId = ClientIdBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(clientId))
        {
            LocalizedDialog.Show("请填写 Azure 应用的 Application (client) ID。", "微软登录",
                MessageBoxButton.OK, MessageBoxImage.Information);
            ClientIdBox.Focus();
            return;
        }

        _settings.Settings.MicrosoftClientId = clientId;
        _settings.Save();
        ShowLogin();
        _ = StartAsync();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
    }

    private void OpenVerify_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(_verificationUri ?? "https://microsoft.com/link")
            {
                UseShellExecute = true
            });
        }
        catch
        {
            LocalizedDialog.Show("无法打开浏览器，请手动访问 https://microsoft.com/link 并输入设备码。", "微软登录",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OpenAzure_Click(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
        }
        catch
        {
            // ignored
        }
    }

    private void ChangeClientId_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ClientIdBox.Text = _settings.Settings.MicrosoftClientId;
        ShowSetup();
    }
}
