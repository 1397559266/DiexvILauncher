using System.Diagnostics;
using System.Windows.Input;
using System.Windows.Media;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.Services;
using Microsoft.Win32;

namespace ILauncher.ViewModels;

public sealed class OverviewViewModel : ObservableObject
{
    private readonly JavaService _javaService;
    private readonly SettingsService _settings;
    private readonly LaunchService _launchService;
    private readonly Action<string, bool> _toast;
    private string _javaText = "检测中…";
    private string _memoryText = "";
    private bool _isExporting;

    public OverviewViewModel(
        GameInstance instance,
        JavaService javaService,
        SettingsService settings,
        LaunchService launchService,
        Action<string, bool> toast,
        ICommand launchCommand,
        ICommand testCommand,
        ICommand openInstanceSettingsCommand)
    {
        Instance = instance;
        _javaService = javaService;
        _settings = settings;
        _launchService = launchService;
        _toast = toast;
        LaunchCommand = launchCommand;
        TestCommand = testCommand;
        OpenInstanceSettingsCommand = openInstanceSettingsCommand;

        OpenVersionFolderCommand = new RelayCommand(() => OpenFolder(instance.Directory));
        OpenGameFolderCommand = new RelayCommand(() => OpenFolder(instance.GameDirectory));
        OpenModsFolderCommand = new RelayCommand(() =>
        {
            Directory.CreateDirectory(instance.ModsDirectory);
            OpenFolder(instance.ModsDirectory);
        });
        OpenLogsFolderCommand = new RelayCommand(() => OpenFolder(System.IO.Path.Combine(instance.RootDirectory, "logs")));
        CopyPathCommand = new RelayCommand(() =>
        {
            try
            {
                System.Windows.Clipboard.SetText(instance.Directory);
                _toast("版本路径已复制。", false);
            }
            catch
            {
                // ignored
            }
        });
        ChangeIconCommand = new RelayCommand(ChangeIcon);
        ResetIconCommand = new RelayCommand(ResetIcon);
        ExportScriptCommand = new RelayCommand(() => _ = ExportScriptAsync(), () => !IsExporting);
        ExportInstanceCommand = new RelayCommand(() => _ = ExportInstanceAsync(), () => !IsExporting);
        RefreshCommand = new AsyncRelayCommand(LoadAsync);

        var overrides = settings.GetInstanceSettings(instance.Id);
        var memory = overrides.MemoryMb ?? settings.Settings.MemoryMb;
        _memoryText = $"{memory:N0} MB";
        _ = LoadAsync();
    }

    public GameInstance Instance { get; }

    public string Name => Instance.Id;
    public string Subtitle => Instance.Subtitle;
    public string Loader => Instance.Loader;
    public string LoaderVersion => string.IsNullOrWhiteSpace(Instance.LoaderVersion) ? Instance.Loader : $"{Instance.Loader} {Instance.LoaderVersion}";
    public string MinecraftVersion => Instance.MinecraftVersion;
    public string VersionType => Instance.TypeLabel;
    public string Path => Instance.Directory;
    public string GameDirectory => Instance.GameDirectory;
    public string ModsDirectory => Instance.ModsDirectory;
    public string IsIsolatedText => Instance.IsIsolated ? "版本隔离" : "全局目录";

    public ImageSource IconSource => InstanceIconService.GetIcon(Instance);
    public string ModSummary => Instance.ModCount > 0
        ? $"{Instance.ModCount} 个模组 · {Instance.EnabledModCount} 启用"
        : "未安装模组";
    public string JavaRequirement => $"Java {Instance.RequiredJavaMajor}+";
    public string LastWriteText => Instance.LastWriteTime.ToString("yyyy-MM-dd HH:mm");
    public string JavaText
    {
        get => _javaText;
        private set => SetProperty(ref _javaText, value);
    }

    public string MemoryText
    {
        get => _memoryText;
        private set => SetProperty(ref _memoryText, value);
    }

    public ICommand LaunchCommand { get; }
    public ICommand TestCommand { get; }
    public ICommand OpenInstanceSettingsCommand { get; }
    public RelayCommand OpenVersionFolderCommand { get; }
    public RelayCommand OpenGameFolderCommand { get; }
    public RelayCommand OpenModsFolderCommand { get; }
    public RelayCommand OpenLogsFolderCommand { get; }
    public RelayCommand CopyPathCommand { get; }
    public RelayCommand ChangeIconCommand { get; }
    public RelayCommand ResetIconCommand { get; }
    public RelayCommand ExportScriptCommand { get; }
    public RelayCommand ExportInstanceCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }

    public bool IsExporting
    {
        get => _isExporting;
        private set
        {
            if (SetProperty(ref _isExporting, value))
            {
                ExportScriptCommand.RaiseCanExecuteChanged();
                ExportInstanceCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public async Task LoadAsync()
    {
        try
        {
            var java = await _javaService.ResolveAsync(Instance.RequiredJavaMajor, _settings.Settings.JavaPath);
            JavaText = java == null ? "未找到可用 Java" : $"Java {java.Major} · {java.Version}";
            var overrides = _settings.GetInstanceSettings(Instance.Id);
            MemoryText = $"{(overrides.MemoryMb ?? _settings.Settings.MemoryMb):N0} MB";
            RefreshModSummary();
        }
        catch
        {
            JavaText = "检测失败";
        }
    }

    public void RefreshModSummary()
    {
        OnPropertyChanged(nameof(ModSummary));
        OnPropertyChanged(nameof(MemoryText));
    }

    private void OpenFolder(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast($"无法打开文件夹：{ex.Message}", true);
        }
    }

    private async Task ExportScriptAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出启动脚本",
            Filter = "批处理脚本 (*.bat)|*.bat",
            FileName = $"{Instance.Id}-启动.bat".Replace(' ', '_')
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsExporting = true;
        try
        {
            var overrides = _settings.GetInstanceSettings(Instance.Id);
            var request = new LaunchRequest
            {
                Instance = Instance,
                PlayerName = _settings.ResolvePlayerName(),
                MemoryMb = overrides.MemoryMb ?? _settings.Settings.MemoryMb,
                JavaPath = string.IsNullOrWhiteSpace(overrides.JavaPath) ? _settings.Settings.JavaPath : overrides.JavaPath,
                Width = overrides.Width ?? _settings.Settings.WindowWidth,
                Height = overrides.Height ?? _settings.Settings.WindowHeight,
                TestMode = false,
                LogDirectory = AppPaths.LogsDirectory
            };
            var plan = await _launchService.CreatePlanAsync(request);
            InstanceExportService.CreateLaunchScript(plan, dialog.FileName);
            _toast("启动脚本已导出。", false);
        }
        catch (Exception ex)
        {
            _toast($"导出启动脚本失败：{ex.Message}", true);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private async Task ExportInstanceAsync()
    {
        var dialog = new SaveFileDialog
        {
            Title = "导出实例",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            FileName = $"{Instance.Id}-export.zip".Replace(' ', '_')
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        IsExporting = true;
        try
        {
            await InstanceExportService.ExportZipAsync(Instance, dialog.FileName, null);
            _toast($"实例已导出到 {dialog.FileName}", false);
        }
        catch (Exception ex)
        {
            _toast($"导出实例失败：{ex.Message}", true);
        }
        finally
        {
            IsExporting = false;
        }
    }

    private void ChangeIcon()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择版本图标",
            Filter = "PNG 图片 (*.png)|*.png"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            InstanceIconService.SetIcon(Instance, dialog.FileName);
            OnPropertyChanged(nameof(IconSource));
            _toast("版本图标已更新，返回主页即可看到。", false);
        }
        catch (Exception ex)
        {
            _toast($"更换图标失败：{ex.Message}", true);
        }
    }

    private void ResetIcon()
    {
        try
        {
            InstanceIconService.ResetIcon(Instance);
            OnPropertyChanged(nameof(IconSource));
            _toast("已恢复默认方块图标。", false);
        }
        catch (Exception ex)
        {
            _toast($"重置图标失败：{ex.Message}", true);
        }
    }
}
