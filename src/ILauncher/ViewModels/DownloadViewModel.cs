using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using Microsoft.Win32;

namespace ILauncher.ViewModels;

public enum DownloadTab
{
    Game,
    Mods,
    ResourcePacks,
    Shaders
}

public sealed class DownloadViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly MojangGameService _mojang;
    private readonly FabricService _fabric;
    private readonly QuiltService _quilt;
    private readonly ModrinthService _modrinth;
    private readonly ModLoaderInstallerService _installers;
    private readonly ModpackService _modpacks;
    private readonly Action<string, bool> _toast;
    private readonly Func<GameInstance?> _selectedInstance;
    private readonly Func<string> _root;
    private readonly Func<Task> _reloadInstances;

    private readonly List<RemoteVersion> _allVersions = new();
    private CancellationTokenSource? _downloadCts;
    private DownloadTab _currentTab = DownloadTab.Game;
    private bool _showSnapshots;
    private bool _isLoadingVersions;
    private bool _isDownloading;
    private bool _isSearching;
    private string _searchQuery = "";
    private string _searchStatus = "";
    private string _progressTitle = "";
    private string _progressMessage = "";
    private double _progressValue;
    private bool _progressIndeterminate;
    private string _targetMinecraftVersion = "";
    private GameInstance? _contextInstance;
    private bool _versionsLoaded;

    public DownloadViewModel(
        SettingsService settings,
        MojangGameService mojang,
        FabricService fabric,
        QuiltService quilt,
        ModrinthService modrinth,
        ModLoaderInstallerService installers,
        ModpackService modpacks,
        Action<string, bool> toast,
        Func<GameInstance?> selectedInstance,
        Func<string> root,
        Func<Task> reloadInstances)
    {
        _settings = settings;
        _mojang = mojang;
        _fabric = fabric;
        _quilt = quilt;
        _modrinth = modrinth;
        _installers = installers;
        _modpacks = modpacks;
        _toast = toast;
        _selectedInstance = selectedInstance;
        _root = root;
        _reloadInstances = reloadInstances;

        GameVersions = new ObservableCollection<RemoteVersion>();
        SearchResults = new ObservableCollection<ModrinthProject>();

        RefreshGameVersionsCommand = new AsyncRelayCommand(LoadVersionsAsync);
        InstallVersionCommand = new AsyncRelayCommand(async parameter =>
        {
            if (parameter is RemoteVersion version)
            {
                await InstallVersionAsync(version);
            }
        });
        InstallFabricCommand = new AsyncRelayCommand(InstallFabricAsync, () => !IsDownloading && _contextInstance != null);
        InstallQuiltCommand = new AsyncRelayCommand(InstallQuiltAsync, () => !IsDownloading && _contextInstance != null);
        InstallForgeCommand = new AsyncRelayCommand(InstallForgeAsync, () => !IsDownloading && _contextInstance != null);
        InstallNeoForgeCommand = new AsyncRelayCommand(InstallNeoForgeAsync, () => !IsDownloading && _contextInstance != null);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsSearching && !IsDownloading);
        ImportModpackCommand = new AsyncRelayCommand(ImportModpackAsync, () => !IsDownloading);
        InstallProjectCommand = new AsyncRelayCommand(async parameter =>
        {
            if (parameter is ModrinthProject project)
            {
                await InstallProjectAsync(project);
            }
        }, _ => !IsDownloading && _contextInstance != null);
        SetTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is string value && Enum.TryParse<DownloadTab>(value, out var tab))
            {
                CurrentTab = tab;
            }
        });
        CancelCommand = new RelayCommand(Cancel, () => IsDownloading);

        RefreshContext();
    }

    public ObservableCollection<RemoteVersion> GameVersions { get; }
    public ObservableCollection<ModrinthProject> SearchResults { get; }

    public AsyncRelayCommand RefreshGameVersionsCommand { get; }
    public AsyncRelayCommand InstallVersionCommand { get; }
    public AsyncRelayCommand InstallFabricCommand { get; }
    public AsyncRelayCommand InstallQuiltCommand { get; }
    public AsyncRelayCommand InstallForgeCommand { get; }
    public AsyncRelayCommand InstallNeoForgeCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public AsyncRelayCommand ImportModpackCommand { get; }
    public AsyncRelayCommand InstallProjectCommand { get; }
    public RelayCommand SetTabCommand { get; }
    public RelayCommand CancelCommand { get; }

    public DownloadTab CurrentTab
    {
        get => _currentTab;
        set
        {
            if (!SetProperty(ref _currentTab, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsGameTab));
            OnPropertyChanged(nameof(IsContentTab));
            OnPropertyChanged(nameof(IsModsTab));
            OnPropertyChanged(nameof(IsResourcePacksTab));
            OnPropertyChanged(nameof(IsShadersTab));
            OnPropertyChanged(nameof(SearchPlaceholder));
            OnPropertyChanged(nameof(ProjectType));
            OnPropertyChanged(nameof(TabTitle));
            OnPropertyChanged(nameof(TabDescription));
            if (value != DownloadTab.Game)
            {
                _ = SearchAsync();
            }
        }
    }

    public bool IsGameTab => CurrentTab == DownloadTab.Game;
    public bool IsContentTab => CurrentTab != DownloadTab.Game;
    public bool IsModsTab => CurrentTab == DownloadTab.Mods;
    public bool IsResourcePacksTab => CurrentTab == DownloadTab.ResourcePacks;
    public bool IsShadersTab => CurrentTab == DownloadTab.Shaders;

    public string ProjectType => CurrentTab switch
    {
        DownloadTab.Mods => "mod",
        DownloadTab.ResourcePacks => "resourcepack",
        DownloadTab.Shaders => "shader",
        _ => "mod"
    };

    public string TabTitle => CurrentTab switch
    {
        DownloadTab.Mods => "模组",
        DownloadTab.ResourcePacks => "资源包",
        DownloadTab.Shaders => "光影",
        _ => "游戏版本"
    };

    public string TabDescription => CurrentTab switch
    {
        DownloadTab.Mods => "从 Modrinth 搜索并安装模组到当前版本。",
        DownloadTab.ResourcePacks => "从 Modrinth 搜索资源包并安装到当前版本。",
        DownloadTab.Shaders => "从 Modrinth 搜索光影并安装到当前版本。",
        _ => "从 Mojang 官方源下载并安装游戏版本，也可以为当前版本安装 Fabric / Quilt。"
    };

    public string SearchPlaceholder => CurrentTab switch
    {
        DownloadTab.Mods => "搜索模组名称、简介或标签…",
        DownloadTab.ResourcePacks => "搜索资源包…",
        DownloadTab.Shaders => "搜索光影…",
        _ => "搜索…"
    };

    public bool ShowSnapshots
    {
        get => _showSnapshots;
        set
        {
            if (SetProperty(ref _showSnapshots, value))
            {
                ApplyVersionFilter();
            }
        }
    }

    public bool IsLoadingVersions
    {
        get => _isLoadingVersions;
        private set => SetProperty(ref _isLoadingVersions, value);
    }

    public bool IsDownloading
    {
        get => _isDownloading;
        private set
        {
            if (SetProperty(ref _isDownloading, value))
            {
                CancelCommand.RaiseCanExecuteChanged();
                InstallFabricCommand.RaiseCanExecuteChanged();
                InstallQuiltCommand.RaiseCanExecuteChanged();
                InstallForgeCommand.RaiseCanExecuteChanged();
                InstallNeoForgeCommand.RaiseCanExecuteChanged();
                InstallProjectCommand.RaiseCanExecuteChanged();
                ImportModpackCommand.RaiseCanExecuteChanged();
                SearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set => SetProperty(ref _isSearching, value);
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public string SearchStatus
    {
        get => _searchStatus;
        private set => SetProperty(ref _searchStatus, value);
    }

    public string ProgressTitle
    {
        get => _progressTitle;
        private set => SetProperty(ref _progressTitle, value);
    }

    public string ProgressMessage
    {
        get => _progressMessage;
        private set => SetProperty(ref _progressMessage, value);
    }

    public double ProgressValue
    {
        get => _progressValue;
        private set => SetProperty(ref _progressValue, value);
    }

    public bool ProgressIndeterminate
    {
        get => _progressIndeterminate;
        private set => SetProperty(ref _progressIndeterminate, value);
    }

    public string TargetMinecraftVersion
    {
        get => _targetMinecraftVersion;
        private set => SetProperty(ref _targetMinecraftVersion, value);
    }

    public string CurrentInstanceText => _contextInstance == null
        ? "未选择版本"
        : $"{_contextInstance.Id} · {_contextInstance.Subtitle}";

    public string FilterSummary
    {
        get
        {
            if (_contextInstance == null)
            {
                return "请先在主页选择一个版本，安装时会使用它的目录";
            }

            var loader = _contextInstance.Loader;
            return $"目标版本：Minecraft {_contextInstance.MinecraftVersion} · {loader}";
        }
    }

    public bool HasSearchResults => SearchResults.Count > 0;

    public void RefreshContext()
    {
        _contextInstance = _selectedInstance();
        TargetMinecraftVersion = _contextInstance?.MinecraftVersion ?? "";
        OnPropertyChanged(nameof(CurrentInstanceText));
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(HasSearchResults));
        InstallFabricCommand.RaiseCanExecuteChanged();
        InstallQuiltCommand.RaiseCanExecuteChanged();
        InstallForgeCommand.RaiseCanExecuteChanged();
        InstallNeoForgeCommand.RaiseCanExecuteChanged();
        InstallProjectCommand.RaiseCanExecuteChanged();
        if (_contextInstance == null && CurrentTab != DownloadTab.Game)
        {
            SearchStatus = "请先在主页选择一个版本，才能把内容安装到对应目录。";
        }

        if (!_versionsLoaded)
        {
            _versionsLoaded = true;
            _ = LoadVersionsAsync();
        }
    }

    private async Task LoadVersionsAsync()
    {
        if (IsLoadingVersions)
        {
            return;
        }

        IsLoadingVersions = true;
        try
        {
            var versions = await _mojang.GetVersionsAsync();
            _allVersions.Clear();
            _allVersions.AddRange(versions);
            ApplyVersionFilter();
            _toast($"已获取 {versions.Count} 个官方版本。", false);
        }
        catch (Exception ex)
        {
            _toast($"获取版本列表失败：{ex.Message}", true);
        }
        finally
        {
            IsLoadingVersions = false;
        }
    }

    private void ApplyVersionFilter()
    {
        GameVersions.Clear();
        foreach (var version in _allVersions.Where(v => ShowSnapshots || v.Type == "release"))
        {
            GameVersions.Add(version);
        }
    }

    private async Task InstallVersionAsync(RemoteVersion version)
    {
        if (IsDownloading)
        {
            return;
        }

        var root = _root();
        if (string.IsNullOrWhiteSpace(root))
        {
            _toast("没有找到 .minecraft 目录。", true);
            return;
        }

        await RunDownloadAsync($"安装 Minecraft {version.Id}", async (report, token) =>
        {
            await _mojang.InstallAsync(version, root, downloadAssets: true, report, token);
            await _reloadInstances();
            _toast($"Minecraft {version.Id} 已安装。", false);
        });
    }

    private async Task InstallFabricAsync()
    {
        if (_contextInstance == null)
        {
            _toast("请先选择一个游戏版本。", true);
            return;
        }

        var instance = _contextInstance;
        await RunDownloadAsync($"安装 Fabric · {instance.MinecraftVersion}", async (report, token) =>
        {
            var id = await _fabric.InstallAsync(instance.MinecraftVersion, null, _root(), report, token);
            await _reloadInstances();
            _toast($"Fabric 实例 {id} 已安装。", false);
        });
    }

    private async Task InstallQuiltAsync()
    {
        if (_contextInstance == null)
        {
            _toast("请先选择一个游戏版本。", true);
            return;
        }

        var instance = _contextInstance;
        await RunDownloadAsync($"安装 Quilt · {instance.MinecraftVersion}", async (report, token) =>
        {
            var id = await _quilt.InstallAsync(instance.MinecraftVersion, null, _root(), report, token);
            await _reloadInstances();
            _toast($"Quilt 实例 {id} 已安装。", false);
        });
    }

    private async Task InstallForgeAsync()
    {
        if (_contextInstance == null)
        {
            _toast("请先选择一个游戏版本。", true);
            return;
        }

        var instance = _contextInstance;
        if (MessageBox.Show(
                $"将为 Minecraft {instance.MinecraftVersion} 运行 Forge 官方安装器。\n安装器会下载数百 MB 文件，继续吗？",
                "安装 Forge", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunDownloadAsync($"安装 Forge · {instance.MinecraftVersion}", async (report, token) =>
        {
            var id = await _installers.InstallForgeAsync(instance.MinecraftVersion, _root(), report, token);
            await _reloadInstances();
            _toast($"Forge 实例 {id} 已安装。", false);
        });
    }

    private async Task InstallNeoForgeAsync()
    {
        if (_contextInstance == null)
        {
            _toast("请先选择一个游戏版本。", true);
            return;
        }

        var instance = _contextInstance;
        if (MessageBox.Show(
                $"将为 Minecraft {instance.MinecraftVersion} 运行 NeoForge 官方安装器。\n安装器会下载数百 MB 文件，继续吗？",
                "安装 NeoForge", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
        {
            return;
        }

        await RunDownloadAsync($"安装 NeoForge · {instance.MinecraftVersion}", async (report, token) =>
        {
            var id = await _installers.InstallNeoForgeAsync(instance.MinecraftVersion, _root(), report, token);
            await _reloadInstances();
            _toast($"NeoForge 实例 {id} 已安装。", false);
        });
    }

    private async Task ImportModpackAsync()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 Modrinth 整合包",
            Filter = "Modrinth 整合包 (*.mrpack)|*.mrpack|ZIP 压缩包 (*.zip)|*.zip"
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        var root = _root();
        if (string.IsNullOrWhiteSpace(root))
        {
            _toast("没有找到 .minecraft 目录。", true);
            return;
        }

        await RunDownloadAsync("导入整合包", async (report, token) =>
        {
            var id = await _modpacks.InstallAsync(dialog.FileName, root, report, token);
            await _reloadInstances();
            _toast($"整合包已安装为 {id}。", false);
        });
    }

    private async Task SearchAsync()
    {
        if (IsSearching || CurrentTab == DownloadTab.Game)
        {
            return;
        }

        IsSearching = true;
        SearchStatus = "正在搜索 Modrinth…";
        try
        {
            var loader = CurrentTab == DownloadTab.Mods ? NormalizeLoader(_contextInstance?.Loader) : null;
            var results = await _modrinth.SearchAsync(SearchQuery, ProjectType,
                _contextInstance?.MinecraftVersion, loader);
            SearchResults.Clear();
            foreach (var project in results)
            {
                SearchResults.Add(project);
            }

            OnPropertyChanged(nameof(HasSearchResults));
            SearchStatus = results.Count == 0
                ? "没有找到匹配的项目，试试换一个关键词。"
                : $"找到 {results.Count} 个项目 · 来源 Modrinth";
        }
        catch (Exception ex)
        {
            SearchStatus = $"搜索失败：{ex.Message}";
        }
        finally
        {
            IsSearching = false;
            InstallProjectCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task InstallProjectAsync(ModrinthProject project)
    {
        if (_contextInstance == null)
        {
            _toast("请先选择一个游戏版本。", true);
            return;
        }

        var instance = _contextInstance;
        var destination = CurrentTab switch
        {
            DownloadTab.ResourcePacks => Path.Combine(instance.GameDirectory, "resourcepacks"),
            DownloadTab.Shaders => Path.Combine(instance.GameDirectory, "shaderpacks"),
            _ => instance.ModsDirectory
        };
        var loader = CurrentTab == DownloadTab.Mods ? NormalizeLoader(instance.Loader) : null;

        await RunDownloadAsync($"安装 {project.Title}", async (report, token) =>
        {
            var path = await _modrinth.InstallAsync(project, destination, instance.MinecraftVersion, loader, report, token);
            _toast($"已安装到 {Path.GetFileName(path)}，重启游戏后生效。", false);
        });
    }

    private async Task RunDownloadAsync(string title, Func<Action<DownloadProgress>, CancellationToken, Task> action)
    {
        if (IsDownloading)
        {
            return;
        }

        IsDownloading = true;
        ProgressTitle = title;
        ProgressMessage = "正在准备…";
        ProgressValue = 0;
        ProgressIndeterminate = true;
        _downloadCts = new CancellationTokenSource();
        try
        {
            await action(ReportProgress, _downloadCts.Token);
        }
        catch (OperationCanceledException)
        {
            _toast("下载已取消。", false);
        }
        catch (Exception ex)
        {
            _toast($"{title} 失败：{ex.Message}", true);
        }
        finally
        {
            _downloadCts.Dispose();
            _downloadCts = null;
            IsDownloading = false;
            ProgressIndeterminate = false;
        }
    }

    private void ReportProgress(DownloadProgress progress)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            ProgressMessage = string.IsNullOrWhiteSpace(progress.Phase)
                ? progress.Message
                : $"{progress.Phase} · {progress.Message}";
            if (progress.Percent >= 0)
            {
                ProgressValue = progress.Percent;
                ProgressIndeterminate = false;
            }
        }));
    }

    private void Cancel()
    {
        _downloadCts?.Cancel();
    }

    private static string? NormalizeLoader(string? loader) => loader switch
    {
        "Fabric" => "fabric",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        "Quilt" => "quilt",
        _ => null
    };
}
