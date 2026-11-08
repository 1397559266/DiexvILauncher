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

public sealed class DownloadFilterOption : ObservableObject
{
    private bool _isSelected;

    public required string Key { get; init; }
    public required string Title { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public sealed class DownloadViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly MojangGameService _mojang;
    private readonly FabricService _fabric;
    private readonly QuiltService _quilt;
    private readonly ModrinthService _modrinth;
    private readonly McmodService _mcmod = new();
    private readonly ModLoaderInstallerService _installers;
    private readonly ModpackService _modpacks;
    private readonly Action<string, bool> _toast;
    private readonly Func<GameInstance?> _selectedInstance;
    private readonly Func<string> _root;
    private readonly Func<Task> _reloadInstances;

    private readonly List<RemoteVersion> _allVersions = new();
    private CancellationTokenSource? _downloadCts;
    private DownloadTab _currentTab = DownloadTab.Game;
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
    private string _versionSearch = "";
    private DownloadFilterOption? _selectedGameType;
    private DownloadFilterOption? _selectedLoader;
    private DownloadFilterOption? _selectedVersionScope;
    private DownloadFilterOption? _selectedCategory;
    private DownloadFilterOption? _selectedDownloadType;
    private RemoteVersion? _selectedRemoteVersion;
    private DownloadFilterOption? _selectedInstallLoader;
    private string? _selectedLoaderVersion;
    private bool _isLoadingLoaderVersions;
    private string _loaderVersionStatus = "";
    private CancellationTokenSource? _loaderVersionCts;
    private int _searchOffset;
    private int _searchTotal;
    private int _searchVersion;
    private const int SearchPageSize = 20;
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
        GameTypeFilters = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "all", Title = "全部" },
            new() { Key = "release", Title = "正式版", IsSelected = true },
            new() { Key = "snapshot", Title = "快照" },
            new() { Key = "old", Title = "旧版" }
        };
        LoaderFilters = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "current", Title = "当前加载器", IsSelected = true },
            new() { Key = "all", Title = "全部加载器" },
            new() { Key = "fabric", Title = "Fabric" },
            new() { Key = "forge", Title = "Forge" },
            new() { Key = "neoforge", Title = "NeoForge" },
            new() { Key = "quilt", Title = "Quilt" }
        };
        VersionScopeFilters = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "current", Title = "当前版本", IsSelected = true },
            new() { Key = "all", Title = "全部版本" }
        };
        CategoryFilters = new ObservableCollection<DownloadFilterOption>();
        InstallLoaderOptions = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "vanilla", Title = "原版", IsSelected = true },
            new() { Key = "fabric", Title = "Fabric" },
            new() { Key = "quilt", Title = "Quilt" },
            new() { Key = "forge", Title = "Forge" },
            new() { Key = "neoforge", Title = "NeoForge" }
        };
        LoaderVersions = new ObservableCollection<string>();
        _selectedInstallLoader = InstallLoaderOptions[0];
        DownloadTypeOptions = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "Game", Title = "游戏版本", IsSelected = true },
            new() { Key = "Mods", Title = "模组" },
            new() { Key = "ResourcePacks", Title = "资源包" },
            new() { Key = "Shaders", Title = "光影" }
        };
        _selectedDownloadType = DownloadTypeOptions[0];
        _selectedGameType = GameTypeFilters.FirstOrDefault(option => option.IsSelected) ?? GameTypeFilters[0];
        _selectedLoader = LoaderFilters.FirstOrDefault(option => option.IsSelected) ?? LoaderFilters[0];
        _selectedVersionScope = VersionScopeFilters.FirstOrDefault(option => option.IsSelected) ?? VersionScopeFilters[0];

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
        InstallSelectedCommand = new AsyncRelayCommand(InstallSelectedAsync, () => CanInstallSelected);
        SearchCommand = new AsyncRelayCommand(SearchAsync, () => !IsSearching && !IsDownloading);
        OpenLinkCommand = new RelayCommand(parameter =>
        {
            if (parameter is string url && Uri.TryCreate(url, UriKind.Absolute, out var uri))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(uri.AbsoluteUri)
                {
                    UseShellExecute = true
                });
            }
        });
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
        PreviousPageCommand = new AsyncRelayCommand(PreviousPageAsync, () => !IsSearching && SearchPage > 1);
        NextPageCommand = new AsyncRelayCommand(NextPageAsync, () => !IsSearching && SearchPage < SearchTotalPages);
        CancelCommand = new RelayCommand(Cancel, () => IsDownloading);

        RebuildCategoryFilters();
        RefreshContext();
    }

    public ObservableCollection<RemoteVersion> GameVersions { get; }
    public ObservableCollection<ModrinthProject> SearchResults { get; }
    public ObservableCollection<DownloadFilterOption> GameTypeFilters { get; }
    public ObservableCollection<DownloadFilterOption> LoaderFilters { get; }
    public ObservableCollection<DownloadFilterOption> VersionScopeFilters { get; }
    public ObservableCollection<DownloadFilterOption> CategoryFilters { get; }
    public ObservableCollection<DownloadFilterOption> InstallLoaderOptions { get; }
    public ObservableCollection<string> LoaderVersions { get; }
    public ObservableCollection<DownloadFilterOption> DownloadTypeOptions { get; }

    public AsyncRelayCommand RefreshGameVersionsCommand { get; }
    public AsyncRelayCommand InstallVersionCommand { get; }
    public AsyncRelayCommand InstallFabricCommand { get; }
    public AsyncRelayCommand InstallQuiltCommand { get; }
    public AsyncRelayCommand InstallForgeCommand { get; }
    public AsyncRelayCommand InstallNeoForgeCommand { get; }
    public AsyncRelayCommand InstallSelectedCommand { get; }
    public AsyncRelayCommand SearchCommand { get; }
    public RelayCommand OpenLinkCommand { get; }
    public AsyncRelayCommand ImportModpackCommand { get; }
    public AsyncRelayCommand PreviousPageCommand { get; }
    public AsyncRelayCommand NextPageCommand { get; }
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
            var typeOption = DownloadTypeOptions.FirstOrDefault(option => option.Key == value.ToString());
            if (typeOption != null && !ReferenceEquals(_selectedDownloadType, typeOption))
            {
                SelectSingle(DownloadTypeOptions, typeOption);
                _selectedDownloadType = typeOption;
                OnPropertyChanged(nameof(SelectedDownloadType));
            }

            RebuildCategoryFilters();
            OnPropertyChanged(nameof(FilterSummary));
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
        DownloadTab.Mods => "搜索并安装模组到当前版本。",
        DownloadTab.ResourcePacks => "搜索资源包并安装到当前版本。",
        DownloadTab.Shaders => "搜索光影并安装到当前版本。",
        _ => "从 Mojang 官方源下载并安装游戏版本，也可以为当前版本安装 Fabric / Quilt。"
    };

    public string SearchPlaceholder => CurrentTab switch
    {
        DownloadTab.Mods => "搜索模组名称、简介或标签…",
        DownloadTab.ResourcePacks => "搜索资源包…",
        DownloadTab.Shaders => "搜索光影…",
        _ => "搜索…"
    };

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
                InstallSelectedCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CanInstallSelected));
                InstallProjectCommand.RaiseCanExecuteChanged();
                ImportModpackCommand.RaiseCanExecuteChanged();
                SearchCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsSearching
    {
        get => _isSearching;
        private set
        {
            if (SetProperty(ref _isSearching, value))
            {
                OnPropertyChanged(nameof(ShowSearchEmpty));
            }
        }
    }

    public string SearchQuery
    {
        get => _searchQuery;
        set => SetProperty(ref _searchQuery, value);
    }

    public string VersionSearch
    {
        get => _versionSearch;
        set
        {
            if (SetProperty(ref _versionSearch, value))
            {
                ApplyVersionFilter();
            }
        }
    }

    public DownloadFilterOption? SelectedGameType
    {
        get => _selectedGameType;
        set
        {
            if (!SetProperty(ref _selectedGameType, value) || value == null)
            {
                return;
            }

            SelectSingle(GameTypeFilters, value);
            ApplyVersionFilter();
        }
    }

    public DownloadFilterOption? SelectedLoader
    {
        get => _selectedLoader;
        set
        {
            if (!SetProperty(ref _selectedLoader, value) || value == null)
            {
                return;
            }

            SelectSingle(LoaderFilters, value);
            OnPropertyChanged(nameof(FilterSummary));
            if (CurrentTab == DownloadTab.Mods)
            {
                _ = SearchAsync();
            }
        }
    }

    public DownloadFilterOption? SelectedVersionScope
    {
        get => _selectedVersionScope;
        set
        {
            if (!SetProperty(ref _selectedVersionScope, value) || value == null)
            {
                return;
            }

            SelectSingle(VersionScopeFilters, value);
            OnPropertyChanged(nameof(FilterSummary));
            if (CurrentTab != DownloadTab.Game)
            {
                _ = SearchAsync();
            }
        }
    }

    public DownloadFilterOption? SelectedCategory
    {
        get => _selectedCategory;
        set
        {
            if (!SetProperty(ref _selectedCategory, value) || value == null)
            {
                return;
            }

            SelectSingle(CategoryFilters, value);
            if (CurrentTab != DownloadTab.Game)
            {
                _ = SearchAsync();
            }
        }
    }

    public DownloadFilterOption? SelectedDownloadType
    {
        get => _selectedDownloadType;
        set
        {
            if (!SetProperty(ref _selectedDownloadType, value) || value == null)
            {
                return;
            }

            SelectSingle(DownloadTypeOptions, value);
            if (Enum.TryParse<DownloadTab>(value.Key, out var tab))
            {
                CurrentTab = tab;
            }
        }
    }

    public RemoteVersion? SelectedRemoteVersion
    {
        get => _selectedRemoteVersion;
        set
        {
            if (!SetProperty(ref _selectedRemoteVersion, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedVersionTitle));
            OnPropertyChanged(nameof(SelectedVersionSubtitle));
            OnPropertyChanged(nameof(CanInstallSelected));
            InstallSelectedCommand.RaiseCanExecuteChanged();
            _ = ReloadLoaderVersionsAsync();
        }
    }

    public DownloadFilterOption? SelectedInstallLoader
    {
        get => _selectedInstallLoader;
        set
        {
            if (!SetProperty(ref _selectedInstallLoader, value) || value == null)
            {
                return;
            }

            SelectSingle(InstallLoaderOptions, value);
            OnPropertyChanged(nameof(CanInstallSelected));
            InstallSelectedCommand.RaiseCanExecuteChanged();
            _ = ReloadLoaderVersionsAsync();
        }
    }

    public string? SelectedLoaderVersion
    {
        get => _selectedLoaderVersion;
        set
        {
            if (SetProperty(ref _selectedLoaderVersion, value))
            {
                OnPropertyChanged(nameof(CanInstallSelected));
                InstallSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool IsLoadingLoaderVersions
    {
        get => _isLoadingLoaderVersions;
        private set => SetProperty(ref _isLoadingLoaderVersions, value);
    }

    public string LoaderVersionStatus
    {
        get => _loaderVersionStatus;
        private set => SetProperty(ref _loaderVersionStatus, value);
    }

    public string SelectedVersionTitle => SelectedRemoteVersion?.Id ?? "未选择版本";
    public string SelectedVersionSubtitle => SelectedRemoteVersion == null
        ? "从左侧版本列表中选择一个版本"
        : $"{SelectedRemoteVersion.TypeText} · {SelectedRemoteVersion.ReleaseTime:yyyy-MM-dd}";

    public bool CanInstallSelected
        => !IsDownloading &&
           SelectedRemoteVersion != null &&
           (string.Equals(SelectedInstallLoader?.Key, "vanilla", StringComparison.OrdinalIgnoreCase) ||
            !string.IsNullOrWhiteSpace(SelectedLoaderVersion));

    public int SearchPage => _searchTotal <= 0 ? 1 : _searchOffset / SearchPageSize + 1;
    public int SearchTotalPages => Math.Max(1, (int)Math.Ceiling(_searchTotal / (double)SearchPageSize));
    public string SearchPageText => _searchTotal <= 0
        ? "第 1 页"
        : $"第 {SearchPage} / {SearchTotalPages} 页 · 共 {_searchTotal} 个";

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
            var scope = CurrentTab == DownloadTab.Game
                ? SelectedGameType?.Title ?? "全部"
                : SelectedVersionScope?.Title ?? "当前版本";
            var suffix = CurrentTab == DownloadTab.Mods
                ? $" · {SelectedLoader?.Title ?? "当前加载器"}"
                : "";
            return $"目标：Minecraft {_contextInstance.MinecraftVersion} · {loader} · {scope}{suffix}";
        }
    }

    public bool HasSearchResults => SearchResults.Count > 0;
    public bool ShowSearchEmpty => !IsSearching && !HasSearchResults;

    public void RefreshContext()
    {
        _contextInstance = _selectedInstance();
        TargetMinecraftVersion = _contextInstance?.MinecraftVersion ?? "";
        OnPropertyChanged(nameof(CurrentInstanceText));
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowSearchEmpty));
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
        var type = SelectedGameType?.Key ?? "all";
        var search = VersionSearch.Trim();
        GameVersions.Clear();
        foreach (var version in _allVersions)
        {
            var typeMatches = type switch
            {
                "release" => string.Equals(version.Type, "release", StringComparison.OrdinalIgnoreCase),
                "snapshot" => string.Equals(version.Type, "snapshot", StringComparison.OrdinalIgnoreCase),
                "old" => version.Type.StartsWith("old_", StringComparison.OrdinalIgnoreCase),
                _ => true
            };
            if (!typeMatches ||
                (search.Length > 0 && !version.Id.Contains(search, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            GameVersions.Add(version);
        }

        OnPropertyChanged(nameof(FilterSummary));
        var selected = SelectedRemoteVersion;
        if (selected == null || !GameVersions.Contains(selected))
        {
            SelectedRemoteVersion = GameVersions.FirstOrDefault();
        }
    }

    private async Task ReloadLoaderVersionsAsync()
    {
        _loaderVersionCts?.Cancel();
        _loaderVersionCts?.Dispose();
        var cts = _loaderVersionCts = new CancellationTokenSource();
        LoaderVersions.Clear();
        SelectedLoaderVersion = null;
        OnPropertyChanged(nameof(CanInstallSelected));
        InstallSelectedCommand.RaiseCanExecuteChanged();

        var version = SelectedRemoteVersion;
        var loader = SelectedInstallLoader?.Key ?? "vanilla";
        if (version == null)
        {
            LoaderVersionStatus = "请选择左侧的 Minecraft 版本";
            return;
        }

        IsLoadingLoaderVersions = true;
        LoaderVersionStatus = "正在获取版本…";
        try
        {
            var values = new List<string>();
            switch (loader)
            {
                case "vanilla":
                    values.Add("原版");
                    break;
                case "fabric":
                    values.AddRange((await _fabric.GetLoadersAsync(version.Id, cts.Token)).Select(item => item.Version));
                    break;
                case "quilt":
                    values.AddRange((await _quilt.GetLoadersAsync(version.Id, cts.Token)).Select(item => item.Version));
                    break;
                case "forge":
                    values.AddRange(await _installers.GetForgeVersionsAsync(version.Id, cts.Token));
                    break;
                case "neoforge":
                    values.AddRange(await _installers.GetNeoForgeVersionsAsync(version.Id, cts.Token));
                    break;
            }

            if (cts.IsCancellationRequested)
            {
                return;
            }

            foreach (var value in values.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                LoaderVersions.Add(value);
            }

            SelectedLoaderVersion = LoaderVersions.FirstOrDefault();
            LoaderVersionStatus = LoaderVersions.Count == 0
                ? "该加载器没有适用于此版本的版本"
                : $"共 {LoaderVersions.Count} 个版本";
        }
        catch (OperationCanceledException)
        {
            // A newer selection replaced this request.
        }
        catch (Exception ex)
        {
            LoaderVersionStatus = $"获取版本失败：{ex.Message}";
        }
        finally
        {
            if (ReferenceEquals(_loaderVersionCts, cts))
            {
                IsLoadingLoaderVersions = false;
                OnPropertyChanged(nameof(CanInstallSelected));
                InstallSelectedCommand.RaiseCanExecuteChanged();
            }
        }
    }

    private async Task InstallSelectedAsync()
    {
        var version = SelectedRemoteVersion;
        if (version == null)
        {
            _toast("请先从左侧选择一个 Minecraft 版本。", true);
            return;
        }

        var loader = SelectedInstallLoader?.Key ?? "vanilla";
        var loaderVersion = SelectedLoaderVersion;
        var root = _root();
        if (string.IsNullOrWhiteSpace(root))
        {
            _toast("没有找到 .minecraft 目录。", true);
            return;
        }

        var title = string.Equals(loader, "vanilla", StringComparison.OrdinalIgnoreCase)
            ? $"安装 Minecraft {version.Id}"
            : $"安装 {SelectedInstallLoader?.Title} {loaderVersion} · Minecraft {version.Id}";
        await RunDownloadAsync(title, async (report, token) =>
        {
            var baseJson = Path.Combine(root, "versions", version.Id, version.Id + ".json");
            if (!string.Equals(loader, "vanilla", StringComparison.OrdinalIgnoreCase) && !File.Exists(baseJson))
            {
                await _mojang.InstallAsync(version, root, downloadAssets: true, report, token);
            }

            switch (loader)
            {
                case "fabric":
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                    {
                        throw new InvalidOperationException("请选择 Fabric 版本。");
                    }

                    await _fabric.InstallAsync(version.Id, loaderVersion, root, report, token);
                    break;
                case "quilt":
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                    {
                        throw new InvalidOperationException("请选择 Quilt 版本。");
                    }

                    await _quilt.InstallAsync(version.Id, loaderVersion, root, report, token);
                    break;
                case "forge":
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                    {
                        throw new InvalidOperationException("请选择 Forge 版本。");
                    }

                    await _installers.InstallForgeVersionAsync(version.Id, loaderVersion, root, report, token);
                    break;
                case "neoforge":
                    if (string.IsNullOrWhiteSpace(loaderVersion))
                    {
                        throw new InvalidOperationException("请选择 NeoForge 版本。");
                    }

                    await _installers.InstallNeoForgeVersionAsync(version.Id, loaderVersion, root, report, token);
                    break;
                default:
                    await _mojang.InstallAsync(version, root, downloadAssets: true, report, token);
                    break;
            }

            await _reloadInstances();
            _toast($"Minecraft {version.Id} 安装完成。", false);
        });
    }

    private static bool SelectSingle(IEnumerable<DownloadFilterOption> options, DownloadFilterOption selected)
    {
        var changed = false;
        foreach (var option in options)
        {
            var active = ReferenceEquals(option, selected);
            if (option.IsSelected != active)
            {
                option.IsSelected = active;
                changed = true;
            }
        }

        return changed;
    }

    private void RebuildCategoryFilters()
    {
        var options = CurrentTab switch
        {
            DownloadTab.Mods => new (string Key, string Title)[]
            {
                ("all", "全部类型"), ("optimization", "优化"), ("technology", "科技"),
                ("magic", "魔法"), ("adventure", "冒险"), ("decoration", "装饰"),
                ("utility", "辅助"), ("library", "前置库")
            },
            DownloadTab.ResourcePacks => new (string Key, string Title)[]
            {
                ("all", "全部分辨率"), ("16x", "16×"), ("32x", "32×"),
                ("64x", "64×"), ("128x", "128×"), ("256x", "256×")
            },
            DownloadTab.Shaders => new (string Key, string Title)[]
            {
                ("all", "全部风格"), ("realistic", "写实"), ("fantasy", "奇幻"),
                ("cartoon", "卡通"), ("semi-realistic", "半写实"),
                ("vanilla-like", "原版风")
            },
            _ => Array.Empty<(string Key, string Title)>()
        };

        CategoryFilters.Clear();
        for (var index = 0; index < options.Length; index++)
        {
            CategoryFilters.Add(new DownloadFilterOption
            {
                Key = options[index].Key,
                Title = options[index].Title,
                IsSelected = index == 0
            });
        }

        _selectedCategory = CategoryFilters.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedCategory));
    }

    private string? ResolveLoader()
    {
        var key = SelectedLoader?.Key ?? "current";
        return key switch
        {
            "all" => null,
            "current" => NormalizeLoader(_contextInstance?.Loader),
            _ => key
        };
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

    private Task SearchAsync() => SearchAsync(resetPage: true);

    private async Task PreviousPageAsync()
    {
        _searchOffset = Math.Max(0, _searchOffset - SearchPageSize);
        await SearchAsync(resetPage: false);
    }

    private async Task NextPageAsync()
    {
        _searchOffset += SearchPageSize;
        await SearchAsync(resetPage: false);
    }

    private async Task SearchAsync(bool resetPage)
    {
        if (IsSearching || CurrentTab == DownloadTab.Game)
        {
            return;
        }

        if (resetPage)
        {
            _searchOffset = 0;
        }

        IsSearching = true;
        SearchStatus = "正在搜索…";
        var searchVersion = ++_searchVersion;
        try
        {
            var loader = CurrentTab == DownloadTab.Mods ? ResolveLoader() : null;
            var gameVersion = SelectedVersionScope?.Key == "all" ? null : _contextInstance?.MinecraftVersion;
            var category = SelectedCategory?.Key is null or "all" ? null : SelectedCategory.Key;
            var result = await _modrinth.SearchAsync(SearchQuery, ProjectType,
                gameVersion, loader, category, _searchOffset, SearchPageSize);
            _searchOffset = result.Offset;
            _searchTotal = result.TotalHits;
            SearchResults.Clear();
            foreach (var project in result.Hits)
            {
                SearchResults.Add(project);
            }

            OnPropertyChanged(nameof(HasSearchResults));
            OnPropertyChanged(nameof(ShowSearchEmpty));
            OnPropertyChanged(nameof(SearchPage));
            OnPropertyChanged(nameof(SearchTotalPages));
            OnPropertyChanged(nameof(SearchPageText));
            SearchStatus = result.Hits.Count == 0
                ? "没有找到匹配的项目，试试换一个关键词。"
                : CurrentTab == DownloadTab.Mods
                    ? $"找到 {result.TotalHits} 个项目 · 正在匹配 MC百科…"
                    : $"找到 {result.TotalHits} 个项目";
            if (CurrentTab == DownloadTab.Mods && result.Hits.Count > 0)
            {
                _ = EnrichMcmodAsync(result.Hits, searchVersion);
            }
        }
        catch (Exception ex)
        {
            SearchStatus = $"搜索失败：{ex.Message}";
        }
        finally
        {
            IsSearching = false;
            InstallProjectCommand.RaiseCanExecuteChanged();
            PreviousPageCommand.RaiseCanExecuteChanged();
            NextPageCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task EnrichMcmodAsync(IReadOnlyList<ModrinthProject> hits, int searchVersion)
    {
        foreach (var project in hits)
        {
            if (searchVersion != _searchVersion)
            {
                return;
            }

            var info = await _mcmod.LookupAsync(project.Title);
            if (searchVersion != _searchVersion)
            {
                return;
            }

            if (info == null)
            {
                continue;
            }

            var index = SearchResults
                .Select((item, i) => (item, i))
                .FirstOrDefault(pair => pair.item.Id == project.Id)
                .i;
            if (index < 0 || index >= SearchResults.Count || SearchResults[index].Id != project.Id)
            {
                continue;
            }

            SearchResults[index] = project with { ChineseName = info.Name, McmodUrl = info.Url };
        }

        if (searchVersion == _searchVersion)
        {
            SearchStatus = $"找到 {_searchTotal} 个项目";
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



