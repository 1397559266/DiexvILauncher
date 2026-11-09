using System.Windows;
using System.Windows.Threading;
using ILauncher.Core;
using ILauncher.Core.Minecraft;
using ILauncher.Core.Services;
using ILauncher.Localization;
using ILauncher.Services;
using ILauncher.ViewModels;

namespace ILauncher;

public partial class App : Application
{
    public SkinService Skins { get; private set; } = null!;

    public MojangProfileService Mojang { get; } = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        LanguageService.Initialize();
        Skins = new SkinService();

        var window = new MainWindow();
        var viewModel = new MainViewModel();
        window.DataContext = viewModel;
        MainWindow = window;
        window.Loaded += (_, _) => LanguageService.SetLanguage(viewModel.SettingsService.Settings.UiLanguage);
        window.Show();
        _ = viewModel.InitializeAsync();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        try
        {
            AppPaths.EnsureCreated();
            File.AppendAllText(Path.Combine(AppPaths.LogsDirectory, "launcher-errors.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {e.Exception}\n\n");
        }
        catch
        {
            // ignored
        }

        LocalizedDialog.Show(
            $"启动器遇到未处理的错误：\n\n{e.Exception.Message}\n\n详细信息已写入日志。",
            "DiexvMinecraftLauncher",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
