using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using ILauncher.Core;
using ILauncher.Core.Services;
using ILauncher.Localization;
using Microsoft.Win32;

namespace ILauncher.ViewModels;

public sealed class SettingsViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly JavaService _javaService;
    private readonly JavaDownloadService _javaDownload = new();
    private readonly Action<string, bool> _toast;
    private readonly Action? _saved;
    private string _rootPath;
    private string _playerName;
    private int _memoryMb;
    private int _width;
    private int _height;
    private bool _closeLauncher;
    private JavaOption? _selectedJava;
    private string _javaSummary = "跟随系统自动选择";
    private bool _isDownloadingJava;
    private string _javaDownloadText = "";
    private string _selectedDownloadSource;
    private string _selectedWindowMode;
    private string _selectedUiLanguage;

    public SettingsViewModel(
        SettingsService settings,
        JavaService javaService,
        Action<string, bool> toast,
        Action? saved = null)
    {
        _settings = settings;
        _javaService = javaService;
        _toast = toast;
        _saved = saved;

        _rootPath = settings.Settings.MinecraftRoot;
        _playerName = settings.ResolvePlayerName();
        _memoryMb = settings.Settings.MemoryMb;
        _width = settings.Settings.WindowWidth;
        _height = settings.Settings.WindowHeight;
        _closeLauncher = settings.Settings.CloseLauncherOnGameStart;
        _selectedDownloadSource = string.Equals(settings.Settings.DownloadSource, "mirror",
            StringComparison.OrdinalIgnoreCase)
            ? "镜像源优先"
            : "官方源优先";
        DownloadSources = new[] { "官方源优先", "镜像源优先" };
        WindowModes = new[] { "自定义", "与启动器窗口一致", "默认 854×480" };
        UiLanguages = new[] { "中文", "English" };
        _selectedUiLanguage = string.Equals(settings.Settings.UiLanguage, "en-US",
            StringComparison.OrdinalIgnoreCase)
            ? "English"
            : "中文";
        _selectedWindowMode = settings.Settings.LaunchWindowMode switch
        {
            "launcher" => "与启动器窗口一致",
            "default" => "默认 854×480",
            _ => "自定义"
        };

        JavaOptions = new ObservableCollection<JavaOption>();
        AutoJava = new JavaOption
        {
            Path = "",
            Label = "自动选择（推荐）",
            Detail = "优先使用版本要求的 Java，必要时选择更新的版本",
            IsAuto = true
        };
        JavaOptions.Add(AutoJava);
        AutoJava.IsSelected = string.IsNullOrWhiteSpace(settings.Settings.JavaPath);
        _selectedJava = AutoJava;

        SaveCommand = new RelayCommand(Save);
        BrowseRootCommand = new RelayCommand(BrowseRoot);
        RefreshJavaCommand = new AsyncRelayCommand(LoadJavaAsync);
        BrowseJavaCommand = new AsyncRelayCommand(BrowseJavaAsync);
        RefreshRootCommand = new RelayCommand(() => _saved?.Invoke());
        DownloadJavaCommand = new AsyncRelayCommand(async parameter =>
        {
            if (parameter is string majorText && int.TryParse(majorText, out var major))
            {
                await DownloadJavaAsync(major);
            }
        }, _ => !IsDownloadingJava);

        _ = LoadJavaAsync();
    }

    public ObservableCollection<JavaOption> JavaOptions { get; }
    public JavaOption AutoJava { get; }
    public IReadOnlyList<string> DownloadSources { get; }
    public IReadOnlyList<string> WindowModes { get; }
    public IReadOnlyList<string> UiLanguages { get; }

    public string SelectedUiLanguage
    {
        get => _selectedUiLanguage;
        set => SetProperty(ref _selectedUiLanguage, value);
    }

    public string SelectedWindowMode
    {
        get => _selectedWindowMode;
        set
        {
            if (SetProperty(ref _selectedWindowMode, value))
            {
                OnPropertyChanged(nameof(IsCustomWindowSize));
            }
        }
    }

    public bool IsCustomWindowSize => SelectedWindowMode == "自定义";

    public string SelectedDownloadSource
    {
        get => _selectedDownloadSource;
        set
        {
            if (!SetProperty(ref _selectedDownloadSource, value) || string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            DownloadSourceConfig.PreferMirror = string.Equals(value, "镜像源优先", StringComparison.Ordinal);
        }
    }

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

    public string RootPath
    {
        get => _rootPath;
        set => SetProperty(ref _rootPath, value);
    }

    public string PlayerName
    {
        get => _playerName;
        set => SetProperty(ref _playerName, value);
    }

    public string SkinPath => _settings.Settings.SkinPath;

    public void RefreshSkin() => OnPropertyChanged(nameof(SkinPath));

    public int MemoryMb
    {
        get => _memoryMb;
        set
        {
            if (SetProperty(ref _memoryMb, value))
            {
                _settings.Settings.MemoryMb = Math.Clamp(value, 512, 65536);
                _settings.Save();
                OnPropertyChanged(nameof(MemoryText));
            }
        }
    }

    public string MemoryText => $"{MemoryMb:N0} MB";

    public int Width
    {
        get => _width;
        set => SetProperty(ref _width, value);
    }

    public int Height
    {
        get => _height;
        set => SetProperty(ref _height, value);
    }

    public bool CloseLauncher
    {
        get => _closeLauncher;
        set => SetProperty(ref _closeLauncher, value);
    }

    public string JavaSummary
    {
        get => _javaSummary;
        private set => SetProperty(ref _javaSummary, value);
    }

    public string VersionText => "DiexvILauncher 1.4.0";

    public ICommand SaveCommand { get; }
    public ICommand BrowseRootCommand { get; }
    public ICommand RefreshRootCommand { get; }
    public AsyncRelayCommand RefreshJavaCommand { get; }
    public AsyncRelayCommand BrowseJavaCommand { get; }
    public AsyncRelayCommand DownloadJavaCommand { get; }

    public bool IsDownloadingJava
    {
        get => _isDownloadingJava;
        private set
        {
            if (SetProperty(ref _isDownloadingJava, value))
            {
                DownloadJavaCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(JavaDownloadText));
            }
        }
    }

    public string JavaDownloadText
    {
        get => _javaDownloadText;
        private set => SetProperty(ref _javaDownloadText, value);
    }

    public void SelectJava(JavaOption? option)
    {
        if (option != null)
        {
            SelectedJava = option;
        }
    }

    private async Task DownloadJavaAsync(int major)
    {
        IsDownloadingJava = true;
        JavaDownloadText = $"正在准备 Java {major}…";
        try
        {
            var path = await _javaDownload.InstallAsync(major, progress =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                dispatcher?.BeginInvoke(new Action(() =>
                {
                    JavaDownloadText = string.IsNullOrWhiteSpace(progress.Message)
                        ? progress.Phase
                        : progress.Message;
                }));
            });
            await LoadJavaAsync();
            var option = JavaOptions.FirstOrDefault(o =>
                string.Equals(o.Path, path, StringComparison.OrdinalIgnoreCase));
            if (option != null)
            {
                SelectedJava = option;
            }

            JavaDownloadText = $"Java {major} 已安装：{path}";
            _toast($"Java {major} 下载并安装完成。", false);
        }
        catch (Exception ex)
        {
            JavaDownloadText = $"下载失败：{ex.Message}";
            _toast($"Java 下载失败：{ex.Message}", true);
        }
        finally
        {
            IsDownloadingJava = false;
        }
    }

    private async Task LoadJavaAsync()
    {
        var installed = await _javaService.DiscoverAsync(force: true);
        var current = _selectedJava?.Path ?? _settings.Settings.JavaPath;
        JavaOptions.Clear();
        JavaOptions.Add(AutoJava);
        foreach (var java in installed)
        {
            JavaOptions.Add(new JavaOption
            {
                Path = java.Path,
                Label = $"Java {java.Major}",
                Detail = java.Path,
                Major = java.Major,
                IsSelected = string.Equals(java.Path, current, StringComparison.OrdinalIgnoreCase)
            });
        }

        // Keep a manually selected java.exe in the list even if it is outside
        // the usual installation folders.
        if (!string.IsNullOrWhiteSpace(current) &&
            JavaOptions.All(o => !string.Equals(o.Path, current, StringComparison.OrdinalIgnoreCase)))
        {
            var info = await _javaService.ProbeAsync(current);
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

        var selected = JavaOptions.FirstOrDefault(o => o.IsSelected) ?? AutoJava;
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
        _toast($"已选择 Java {info.Major}，保存设置后生效。", false);
    }

    private void BrowseRoot()
    {
        var dialog = new OpenFolderDialog
        {
            InitialDirectory = Directory.Exists(RootPath) ? RootPath : Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };
        LocalizedDialog.Configure(dialog, "选择 .minecraft 目录");

        if (dialog.ShowDialog() == true)
        {
            RootPath = dialog.FolderName;
        }
    }

    private void Save()
    {
        if (!string.IsNullOrWhiteSpace(RootPath) && !Directory.Exists(Path.Combine(RootPath, "versions")))
        {
            _toast("所选目录下没有 versions 文件夹，请选择正确的 .minecraft 目录。", true);
            return;
        }

        _settings.Settings.MinecraftRoot = RootPath.Trim();
        _settings.Settings.PlayerName = string.IsNullOrWhiteSpace(PlayerName) ? Environment.UserName : PlayerName.Trim();
        _settings.Settings.MemoryMb = Math.Clamp(MemoryMb, 512, 65536);
        _settings.Settings.WindowWidth = Math.Clamp(Width, 320, 7680);
        _settings.Settings.WindowHeight = Math.Clamp(Height, 240, 4320);
        _settings.Settings.LaunchWindowMode = SelectedWindowMode switch
        {
            "与启动器窗口一致" => "launcher",
            "默认 854×480" => "default",
            _ => "custom"
        };
        _settings.Settings.JavaPath = SelectedJava is { IsAuto: false } ? SelectedJava.Path : "";
        _settings.Settings.CloseLauncherOnGameStart = CloseLauncher;
        _settings.Settings.DownloadSource = SelectedDownloadSource == "镜像源优先" ? "mirror" : "official";
        _settings.Settings.UiLanguage = SelectedUiLanguage == "English" ? "en-US" : "zh-CN";
        _settings.Save();
        LanguageService.SetLanguage(_settings.Settings.UiLanguage);

        _toast("启动器设置已保存。", false);
        _saved?.Invoke();
    }
}
