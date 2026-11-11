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
    private string _redirectUri = "";

    public MicrosoftLoginWindow(SettingsService settings)
    {
        InitializeComponent();
        _settings = settings;
        Loaded += OnLoaded;
        Closed += (_, _) => _cts?.Cancel();
    }

    public MicrosoftAccount? Account { get; private set; }

    private string ClientId => _settings.Settings.MicrosoftClientId.Trim();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            ShowSetup();
            return;
        }

        if (string.Equals(_settings.Settings.MicrosoftAuthMode, "device", StringComparison.OrdinalIgnoreCase))
        {
            ShowDevice();
            _ = StartDeviceAsync();
        }
        else
        {
            ShowBrowser();
            _ = StartBrowserAsync(switchAccount: false);
        }
    }

    private int ResolvePort()
    {
        // 首选端口被占用时自动换一个：按 http://localhost 登记的话端口不限。
        return MicrosoftAuthService.FindFreePort(_settings.Settings.MicrosoftRedirectPort);
    }

    private void ShowSetup()
    {
        _cts?.Cancel();
        SetupPanel.Visibility = Visibility.Visible;
        LoginPanel.Visibility = Visibility.Collapsed;
        ContinueButton.Visibility = Visibility.Visible;
        ContinueButton.Content = "保存并登录";
        SetupRedirectText.Text =
            $"http://localhost（推荐，端口不限）    或精确地址 {MicrosoftAuthService.BuildRedirectUri(ResolvePort())}";
    }

    private void ShowBrowser()
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        BrowserPanel.Visibility = Visibility.Visible;
        DevicePanel.Visibility = Visibility.Collapsed;
        ContinueButton.Visibility = Visibility.Collapsed;
        _redirectUri = MicrosoftAuthService.BuildRedirectUri(ResolvePort());
        RedirectText.Text = _redirectUri;
        BrowserActionButton.Content = "重新打开浏览器";
    }

    private void ShowDevice()
    {
        SetupPanel.Visibility = Visibility.Collapsed;
        LoginPanel.Visibility = Visibility.Visible;
        BrowserPanel.Visibility = Visibility.Collapsed;
        DevicePanel.Visibility = Visibility.Visible;
        ContinueButton.Visibility = Visibility.Collapsed;
    }

    private async Task StartBrowserAsync(bool switchAccount)
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        var clientId = ClientId;
        if (string.IsNullOrWhiteSpace(clientId))
        {
            ShowSetup();
            return;
        }

        var port = ResolvePort();
        _redirectUri = MicrosoftAuthService.BuildRedirectUri(port);
        RedirectText.Text = _redirectUri;
        LoginSpinner.Visibility = Visibility.Visible;
        BrowserActionButton.Content = "重新打开浏览器";
        SetStatus("正在检查 Azure 应用的回调地址…", false);
        try
        {
            var selection = await _auth.ResolveRedirectAsync(clientId, port, token);
            token.ThrowIfCancellationRequested();

            switch (selection.Detail)
            {
                case "AADSTS700016":
                    RunFailed("Azure 应用 ID 无效，或应用不在可用目录中。请确认填写的是“应用程序(客户端) ID”。");
                    return;
                case "unauthorized_client":
                    RunFailed("这个 Azure 应用没有启用个人 Microsoft 账户：请在“身份验证 → 支持的账户类型”里选择包含个人账户的类型。");
                    return;
            }

            if (selection.Status == MicrosoftRedirectStatus.NotRegistered)
            {
                RunFailed("Azure 应用还没有登记回调地址。请打开应用注册 → 身份验证 → 添加平台 → " +
                          "移动和桌面应用程序 → 重定向 URI，添加 http://localhost（推荐，端口不限）" +
                          $"或精确地址 {selection.RedirectUri}，保存后点“重试”。");
                return;
            }

            _redirectUri = selection.RedirectUri;
            RedirectText.Text = _redirectUri;
            SetStatus("正在打开浏览器，请在微软页面完成登录…", false);
            var account = await _auth.LoginWithBrowserAsync(clientId, _redirectUri, OpenUrl,
                status => SetStatusFromAnyThread(status), _settings.Settings.MicrosoftLoginHint, switchAccount,
                uri =>
                {
                    _redirectUri = uri;
                    RedirectText.Text = uri;
                }, token);

            if (token.IsCancellationRequested)
            {
                return;
            }

            Account = account;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cts, cts))
            {
                SetStatus("登录已取消。", false);
            }
        }
        catch (TimeoutException ex)
        {
            RunFailed($"{ex.Message} 如果浏览器里显示 AADSTS50011，说明这个地址还没登记到 Azure 应用：{_redirectUri}");
        }
        catch (Exception ex)
        {
            RunFailed(ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                LoginSpinner.Visibility = Visibility.Collapsed;
            }
        }
    }

    private async Task StartDeviceAsync()
    {
        _cts?.Cancel();
        var cts = new CancellationTokenSource();
        _cts = cts;
        var token = cts.Token;
        LoginSpinner.Visibility = Visibility.Visible;
        SetStatus("正在向微软申请设备码…", false);
        try
        {
            var code = await _auth.StartDeviceCodeAsync(ClientId, token);
            CodeText.Text = code.UserCode;
            MessageText.Text = code.Message;
            _verificationUri = code.VerificationUri;
            SetStatus("正在打开浏览器，请输入上面的代码完成登录…", false);
            OpenUrl(code.VerificationUri);

            var account = await _auth.CompleteAsync(ClientId, code, SetStatusFromAnyThread, token);
            if (token.IsCancellationRequested)
            {
                return;
            }

            Account = account;
            DialogResult = true;
        }
        catch (OperationCanceledException)
        {
            if (ReferenceEquals(_cts, cts))
            {
                SetStatus("登录已取消。", false);
            }
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(_cts, cts))
            {
                SetStatus(ex.Message, true);
            }
        }
        finally
        {
            if (ReferenceEquals(_cts, cts))
            {
                LoginSpinner.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void RunFailed(string message)
    {
        SetStatus(message, true);
        BrowserActionButton.Content = "重试";
    }

    private void SetStatus(string text, bool error)
    {
        StatusText.Text = text;
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, error ? "BrushDanger" : "BrushAccent");
    }

    private void SetStatusFromAnyThread(string text) =>
        Dispatcher.Invoke(() => SetStatus(text, false));

    private void BrowserAction_Click(object sender, RoutedEventArgs e) =>
        _ = StartBrowserAsync(switchAccount: false);

    private void SwitchAccount_Click(object sender, RoutedEventArgs e) =>
        _ = StartBrowserAsync(switchAccount: true);

    private void UseDeviceCode_Click(object sender, RoutedEventArgs e)
    {
        _settings.Settings.MicrosoftAuthMode = "device";
        _settings.Save();
        ShowDevice();
        _ = StartDeviceAsync();
    }

    private void UseBrowser_Click(object sender, RoutedEventArgs e)
    {
        _settings.Settings.MicrosoftAuthMode = "browser";
        _settings.Save();
        ShowBrowser();
        _ = StartBrowserAsync(switchAccount: false);
    }

    private void CopyRedirect_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_redirectUri);
            SetStatus("已复制重定向 URI。", false);
        }
        catch (Exception ex)
        {
            SetStatus($"复制失败：{ex.Message}", true);
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
        _settings.Settings.MicrosoftAuthMode = "browser";
        _settings.Save();
        ShowBrowser();
        _ = StartBrowserAsync(switchAccount: false);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        if (DialogResult != true)
        {
            DialogResult = false;
        }
    }

    private void OpenVerify_Click(object sender, RoutedEventArgs e) =>
        OpenUrl(_verificationUri ?? "https://microsoft.com/link");

    private void OpenAzure_Click(object sender, RequestNavigateEventArgs e) =>
        OpenUrl(e.Uri.AbsoluteUri);

    private void ChangeClientId_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ClientIdBox.Text = _settings.Settings.MicrosoftClientId;
        ShowSetup();
    }

    private void OpenUrl(string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"无法打开浏览器：{ex.Message}。请手动访问 {url}", true);
        }
    }
}
