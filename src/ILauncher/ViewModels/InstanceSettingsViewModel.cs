using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.Localization;
using Microsoft.Win32;

namespace ILauncher.ViewModels;

public sealed class InstanceSettingsViewModel : ObservableObject
{
    private readonly GameInstance _instance;
    private readonly SettingsService _settings;
    private readonly JavaService _javaService;
    private readonly Action<string, bool> _toast;
    private readonly Action? _saved;
    private int _memoryMb;
    private int _memoryBaseline;
    private string _extraJvmArgs = "";
    private bool _useCustomResolution;
    private int _width;
    private int _height;
    private string _javaSummary = "";
    private JavaOption? _selectedJava;
    private readonly LoaderUpdateService _loaderUpdates;
    private LoaderVersionOption? _selectedLoaderVersion;
    private bool _isLoaderBusy;
    private string _loaderStatus = "";

    public InstanceSettingsViewModel(
        GameInstance instance,
        SettingsService settings,
        JavaService javaService,
        Action<string, bool> toast,
        Action? saved = null)
    {
        _instance = instance;
        _settings = settings;
        _javaService = javaService;
        _toast = toast;
        _saved = saved;

        var overrides = settings.GetInstanceSettings(instance.Id);
        _memoryBaseline = settings.ResolveMemoryMb(instance.Id, instance.ModCount,
            !string.Equals(instance.Loader, "原版", StringComparison.Ordinal),
            (instance.Loader + instance.LoaderVersion + instance.Id)
                .Contains("OptiFine", StringComparison.OrdinalIgnoreCase));
        _memoryMb = overrides.MemoryMb ?? _memoryBaseline;
        _useCustomResolution = overrides.Width is > 0 && overrides.Height is > 0;
        _width = overrides.Width ?? settings.Settings.WindowWidth;
        _height = overrides.Height ?? settings.Settings.WindowHeight;

        JavaOptions = new ObservableCollection<JavaOption>();
        SaveCommand = new RelayCommand(Save);
        ResetCommand = new RelayCommand(Reset);
        RefreshJavaCommand = new AsyncRelayCommand(LoadJavaAsync);
        BrowseJavaCommand = new AsyncRelayCommand(BrowseJavaAsync);
        AutoOption = new JavaOption
        {
            Path = "",
            Label = "自动选择（推荐）",
            Detail = $"根据版本需求自动挑选 Java {instance.RequiredJavaMajor}+",
            Major = 0,
            IsAuto = true
        };
        JavaOptions.Add(AutoOption);
        AutoOption.IsSelected = string.IsNullOrWhiteSpace(overrides.JavaPath);
        _selectedJava = AutoOption;
        _javaSummary = overrides.JavaPath is { Length: > 0 } custom ? custom : "跟随全局设置";
        _extraJvmArgs = overrides.ExtraJvmArgs;
        _ = LoadJavaAsync();

        _loaderUpdates = new LoaderUpdateService(javaService, settings);
        ApplyLoaderCommand = new AsyncRelayCommand(ApplyLoaderAsync, () => CanUpdateLoader && !IsLoaderBusy);
        RefreshLoaderVersionsCommand = new AsyncRelayCommand(LoadLoaderVersionsAsync);
        if (CanUpdateLoader)
        {
            _ = LoadLoaderVersionsAsync();
        }
    }

    public GameInstance Instance => _instance;

    public ObservableCollection<JavaOption> JavaOptions { get; }

    public JavaOption AutoOption { get; }

    public JavaOption? SelectedJava
    {
        get => _selectedJava;
        set
        {
            if (!SetProperty(ref _selectedJava, value) || value == null)
            {
                return;
            }

            foreach (var item in JavaOptions)
            {
                item.IsSelected = ReferenceEquals(item, value);
            }

            JavaSummary = value.IsAuto ? "自动选择最合适的 Java" : value.Path;
        }
    }

    /// <summary>原版实例没有加载器，卡片直接隐藏。</summary>
    public bool CanUpdateLoader => LoaderUpdateService.CanUpdate(_instance);

    public string LoaderCurrentText => CanUpdateLoader
        ? $"当前 {_instance.Loader} {_instance.LoaderVersion}"
        : "原版";

    public ObservableCollection<LoaderVersionOption> LoaderVersions { get; } = new();

    public LoaderVersionOption? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set => SetProperty(ref _selectedLoaderVersion, value);
    }

    public bool IsLoaderBusy
    {
        get => _isLoaderBusy;
        private set
        {
            if (SetProperty(ref _isLoaderBusy, value))
            {
                OnPropertyChanged(nameof(IsLoaderIdle));
                ApplyLoaderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoaderIdle => !_isLoaderBusy;

    public string LoaderStatus
    {
        get => _loaderStatus;
        private set => SetProperty(ref _loaderStatus, value);
    }

    public AsyncRelayCommand ApplyLoaderCommand { get; }

    public AsyncRelayCommand RefreshLoaderVersionsCommand { get; }

    public string VersionName => _instance.Id;
    public string VersionSubtitle => _instance.Subtitle;
    public string JavaRequirement => LanguageService.CurrentLanguage == LanguageService.English
        ? $"This version requires Java {_instance.RequiredJavaMajor}+"
        : $"此版本需要 Java {_instance.RequiredJavaMajor}+";
    public string PlayerName => _settings.ResolvePlayerName();
    public string GameDirectory => _instance.GameDirectory;

    /// <summary>该版本的 mods 目录，供「快速添加 Agent 参数」面板使用。</summary>
    public string ModsDirectory => _instance.ModsDirectory;
    public string VersionDirectory => _instance.Directory;
    public string IsolationText => LanguageService.Translate(_instance.IsIsolated
        ? "已启用版本隔离（模组、存档位于版本目录）"
        : "未隔离（使用 .minecraft 根目录）");

    public string JavaSummary
    {
        get => _javaSummary;
        private set => SetProperty(ref _javaSummary, LanguageService.Translate(value));
    }

    public int MemoryMb
    {
        get => _memoryMb;
        set
        {
            if (SetProperty(ref _memoryMb, value))
            {
                OnPropertyChanged(nameof(MemoryText));
            }
        }
    }

    public string MemoryText => MemoryMb == _memoryBaseline &&
                                string.Equals(_settings.Settings.MemoryMode, "auto", StringComparison.OrdinalIgnoreCase)
        ? $"{MemoryMb:N0} MB（自动）"
        : $"{MemoryMb:N0} MB";

    /// <summary>版本级额外 JVM 参数；留空表示跟随全局。</summary>
    public string ExtraJvmArgs
    {
        get => _extraJvmArgs;
        set => SetProperty(ref _extraJvmArgs, value ?? "");
    }

    public bool UseCustomResolution
    {
        get => _useCustomResolution;
        set
        {
            if (SetProperty(ref _useCustomResolution, value))
            {
                OnPropertyChanged(nameof(ResolutionText));
            }
        }
    }

    public int Width
    {
        get => _width;
        set
        {
            if (SetProperty(ref _width, value))
            {
                OnPropertyChanged(nameof(ResolutionText));
            }
        }
    }

    public int Height
    {
        get => _height;
        set
        {
            if (SetProperty(ref _height, value))
            {
                OnPropertyChanged(nameof(ResolutionText));
            }
        }
    }

    public string ResolutionText => UseCustomResolution
        ? $"{Width} × {Height}"
        : LanguageService.Translate("跟随游戏默认");

    public ICommand SaveCommand { get; }
    public ICommand ResetCommand { get; }
    public AsyncRelayCommand RefreshJavaCommand { get; }
    public AsyncRelayCommand BrowseJavaCommand { get; }

    private async Task LoadJavaAsync()
    {
        var installed = await _javaService.DiscoverAsync(force: true);
        var preferredPath = _settings.GetInstanceSettings(_instance.Id).JavaPath;
        JavaOptions.Clear();
        JavaOptions.Add(AutoOption);
        foreach (var java in installed)
        {
            JavaOptions.Add(new JavaOption
            {
                Path = java.Path,
                Label = $"Java {java.Major}",
                Detail = java.Path,
                Major = java.Major,
                IsSelected = string.Equals(java.Path, preferredPath, StringComparison.OrdinalIgnoreCase)
            });
        }

        if (!string.IsNullOrWhiteSpace(preferredPath) &&
            JavaOptions.All(o => !string.Equals(o.Path, preferredPath, StringComparison.OrdinalIgnoreCase)))
        {
            var info = await _javaService.ProbeAsync(preferredPath);
            if (info != null)
            {
                JavaOptions.Add(new JavaOption
                {
                    Path = info.Path,
                    Label = $"Java {info.Major}",
                    Detail = info.Path,
                    Major = info.Major,
                    IsSelected = true
                });
            }
        }

        var selected = JavaOptions.FirstOrDefault(o => o.IsSelected) ?? AutoOption;
        SelectedJava = selected;
        JavaSummary = selected.IsAuto ? "自动选择最合适的 Java" : selected.Path;
        OnPropertyChanged(nameof(JavaSummary));
    }

    private async Task BrowseJavaAsync()
    {
        var dialog = new OpenFileDialog { CheckFileExists = true, Multiselect = false };
        LocalizedDialog.Configure(dialog, "选择 java.exe",
            "Java executable (java.exe)|java.exe|可执行文件 (*.exe)|*.exe|所有文件 (*.*)|*.*");

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var info = await _javaService.ProbeAsync(dialog.FileName);
        if (info == null)
        {
            _toast("所选文件不是可用的 Java（java -version 执行失败）。", true);
            return;
        }

        var option = JavaOptions.FirstOrDefault(o =>
            string.Equals(o.Path, info.Path, StringComparison.OrdinalIgnoreCase));
        if (option == null)
        {
            option = new JavaOption
            {
                Path = info.Path,
                Label = $"Java {info.Major}",
                Detail = info.Path,
                Major = info.Major
            };
            JavaOptions.Insert(1, option);
        }

        SelectedJava = option;
        JavaSummary = option.Path;
        OnPropertyChanged(nameof(JavaSummary));
        _toast($"已选择 Java {info.Major}，保存版本设置后生效。", false);
    }

    public void SelectJava(JavaOption? option)
    {
        if (option != null)
        {
            SelectedJava = option;
        }
    }

    private void Save()
    {
        var overrides = new InstanceOverrides
        {
            JavaPath = SelectedJava is { IsAuto: false } ? SelectedJava.Path : "",
            MemoryMb = MemoryMb == _memoryBaseline ? null : Math.Clamp(MemoryMb, 512, 65536),
            Width = UseCustomResolution ? Math.Clamp(Width, 320, 7680) : null,
            ExtraJvmArgs = ExtraJvmArgs.Trim(),
            Height = UseCustomResolution ? Math.Clamp(Height, 240, 4320) : null
        };

        _settings.SaveInstanceSettings(_instance.Id, overrides);
        _toast("版本设置已保存，下次启动生效。", false);
        _saved?.Invoke();
    }

    private void Reset()
    {
        _settings.Settings.Instances.Remove(_instance.Id);
        _settings.Save();
        MemoryMb = _settings.Settings.MemoryMb;
        UseCustomResolution = false;
        Width = _settings.Settings.WindowWidth;
        Height = _settings.Settings.WindowHeight;
        ExtraJvmArgs = "";
        AutoOption.IsSelected = true;
        SelectJava(AutoOption);
        _toast("已恢复为全局设置。", false);
        _saved?.Invoke();
    }

    /// <summary>实例里的 LoaderVersion 可能是「1.20.1-47.4.20」「forge-47.4.20-1.20.1」等带前缀的写法。</summary>
    private bool IsCurrentLoaderVersion(string version) =>
        !string.IsNullOrWhiteSpace(version) &&
        (string.Equals(version, _instance.LoaderVersion, StringComparison.OrdinalIgnoreCase) ||
         _instance.LoaderVersion.EndsWith("-" + version, StringComparison.OrdinalIgnoreCase) ||
         _instance.LoaderVersion.Contains(version, StringComparison.OrdinalIgnoreCase));

    private async Task LoadLoaderVersionsAsync()
    {
        if (!CanUpdateLoader)
        {
            return;
        }

        LoaderStatus = "正在获取加载器版本列表…";
        try
        {
            var versions = await _loaderUpdates.GetVersionsAsync(_instance);
            LoaderVersions.Clear();
            foreach (var version in versions)
            {
                LoaderVersions.Add(version);
            }

            // 默认选中最新版本；当前版本在列表里的话也标记出来。
            SelectedLoaderVersion = LoaderVersions.FirstOrDefault(v =>
                                        string.Equals(v.Version, _instance.LoaderVersion, StringComparison.OrdinalIgnoreCase))
                                    ?? LoaderVersions.FirstOrDefault();
            LoaderStatus = versions.Count == 0
                ? $"没有找到适用于 {_instance.MinecraftVersion} 的 {_instance.Loader} 版本。"
                : $"共 {versions.Count} 个版本；当前 {_instance.LoaderVersion}。";
        }
        catch (Exception ex)
        {
            LoaderStatus = $"获取加载器版本失败：{ex.Message}";
        }
    }

    private async Task ApplyLoaderAsync()
    {
        if (IsLoaderBusy || SelectedLoaderVersion == null)
        {
            return;
        }

        var target = SelectedLoaderVersion.Version;
        if (IsCurrentLoaderVersion(target))
        {
            _toast("选中的就是当前版本。", false);
            return;
        }

        var confirm = LocalizedDialog.Show(
            $"把 {_instance.Id} 的 {_instance.Loader} 从 {_instance.LoaderVersion} 更新到 {target}？\n\n" +
            "模组、存档和设置都会保留；原来的版本 JSON 会备份为 " +
            $"{_instance.Id}.json.bak，更新后需要重启游戏。",
            "更新模组加载器", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        IsLoaderBusy = true;
        LoaderStatus = "正在安装新的加载器…";
        try
        {
            var progress = new Action<DownloadProgress>(report =>
            {
                var text = string.IsNullOrWhiteSpace(report.Message) ? "正在安装…" : report.Message;
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess())
                {
                    LoaderStatus = text;
                }
                else
                {
                    dispatcher.BeginInvoke(() => LoaderStatus = text);
                }
            });
            await _loaderUpdates.UpdateAsync(_instance, target, progress);
            LoaderStatus = $"{_instance.Loader} 已更新到 {target}，重启游戏后生效。";
            _toast($"{_instance.Loader} 已更新到 {target}。", false);
            _saved?.Invoke();
        }
        catch (Exception ex)
        {
            LoaderStatus = $"更新失败：{ex.Message}";
            _toast($"更新加载器失败：{ex.Message}", true);
        }
        finally
        {
            IsLoaderBusy = false;
        }
    }
}
