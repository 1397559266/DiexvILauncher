using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.Models;
using ILauncher.Views;
using Microsoft.Win32;

namespace ILauncher.ViewModels;

public enum DownloadTab
{
    Game,
    Mods,
    Modpacks,
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
    private readonly Func<IReadOnlyList<GameInstance>> _allInstances;
    private readonly Func<string> _root;
    private readonly Func<Task> _reloadInstances;

    private readonly List<RemoteVersion> _allVersions = new();
    private CancellationTokenSource? _downloadCts;
    private DownloadTab _currentTab = DownloadTab.Game;
    private bool _isLoadingVersions;
    private bool _isDownloading;
    private bool _isSearching;
    private string _searchQuery = "";
    private string _effectiveSearchQuery = "";
    private string _searchStatus = "";
    private string _progressTitle = "";
    private string _progressMessage = "";
    private double _progressValue;
    private bool _progressIndeterminate;
    private string _targetMinecraftVersion = "";
    private string _versionSearch = "";
    private DownloadFilterOption? _selectedGameType;
    private DownloadFilterOption? _selectedLoader;
    private DownloadFilterOption? _selectedSort;
    private DownloadFilterOption? _selectedVersionScope;
    private DownloadFilterOption? _selectedSearchGameVersion;
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
    private readonly Dictionary<string, CachedSearchPage> _searchCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IReadOnlyList<JarDependency>> _jarDependencyCache = new(StringComparer.Ordinal);
    private readonly Queue<string> _searchCacheOrder = new();
    private const int SearchPageSize = 20;
    private GameInstance? _contextInstance;
    private GameInstance? _selectedTargetInstance;
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
        Func<IReadOnlyList<GameInstance>> allInstances,
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
        _allInstances = allInstances;
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
        SortFilters = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "relevance", Title = "相关度", IsSelected = true },
            new() { Key = "downloads", Title = "下载量" },
            new() { Key = "updated", Title = "最近更新" },
            new() { Key = "newest", Title = "最新发布" }
        };
        VersionScopeFilters = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "current", Title = "当前版本", IsSelected = true },
            new() { Key = "all", Title = "全部版本" }
        };
        SearchGameVersionFilters = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "current", Title = "当前目标版本", IsSelected = true },
            new() { Key = "all", Title = "全部版本" }
        };
        CategoryFilters = new ObservableCollection<DownloadFilterOption>();
        InstallLoaderOptions = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "vanilla", Title = "原版", IsSelected = true },
            new() { Key = "forge", Title = "Forge" },
            new() { Key = "neoforge", Title = "NeoForge" },
            new() { Key = "fabric", Title = "Fabric" },
            new() { Key = "quilt", Title = "Quilt" }
        };
        LoaderVersions = new ObservableCollection<string>();
        _selectedInstallLoader = InstallLoaderOptions[0];
        DownloadTypeOptions = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "Game", Title = "游戏版本", IsSelected = true },
            new() { Key = "Mods", Title = "模组" },
            new() { Key = "Modpacks", Title = "整合包" },
            new() { Key = "ResourcePacks", Title = "资源包" },
            new() { Key = "Shaders", Title = "光影" }
        };
        _selectedDownloadType = DownloadTypeOptions[0];
        _selectedGameType = GameTypeFilters.FirstOrDefault(option => option.IsSelected) ?? GameTypeFilters[0];
        _selectedLoader = LoaderFilters.FirstOrDefault(option => option.IsSelected) ?? LoaderFilters[0];
        _selectedSort = SortFilters[0];
        _selectedVersionScope = VersionScopeFilters.FirstOrDefault(option => option.IsSelected) ?? VersionScopeFilters[0];
        _selectedSearchGameVersion = SearchGameVersionFilters[0];

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
        OpenVersionPickerCommand = new AsyncRelayCommand(async parameter =>
        {
            if (parameter is ModrinthProject project)
            {
                await OpenVersionPickerAsync(project);
            }
        }, _ => !IsDownloading &&
                (_contextInstance != null || CurrentTab == DownloadTab.Modpacks));
        OpenProjectDetailsCommand = new RelayCommand(parameter =>
        {
            if (parameter is ModrinthProject project)
            {
                new ProjectDetailWindow(project, _modrinth)
                {
                    Owner = Application.Current.MainWindow
                }.ShowDialog();
            }
        });
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
        LoadSearchCacheFromDisk();
        RefreshContext();
    }

    public ObservableCollection<RemoteVersion> GameVersions { get; }
    public ObservableCollection<ModrinthProject> SearchResults { get; }
    public ObservableCollection<DownloadFilterOption> GameTypeFilters { get; }
    public ObservableCollection<DownloadFilterOption> LoaderFilters { get; }
    public ObservableCollection<DownloadFilterOption> SortFilters { get; }
    public ObservableCollection<DownloadFilterOption> VersionScopeFilters { get; }
    public ObservableCollection<DownloadFilterOption> SearchGameVersionFilters { get; }
    public ObservableCollection<DownloadFilterOption> CategoryFilters { get; }
    public ObservableCollection<GameInstance> TargetInstances { get; } = new();
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
    public AsyncRelayCommand OpenVersionPickerCommand { get; }
    public RelayCommand OpenProjectDetailsCommand { get; }
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
        OnPropertyChanged(nameof(IsModpacksTab));
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
    public bool IsModpacksTab => CurrentTab == DownloadTab.Modpacks;

    public string ProjectType => CurrentTab switch
    {
        DownloadTab.Mods => "mod",
        DownloadTab.Modpacks => "modpack",
        DownloadTab.ResourcePacks => "resourcepack",
        DownloadTab.Shaders => "shader",
        _ => "mod"
    };

    public string TabTitle => CurrentTab switch
    {
        DownloadTab.Mods => "模组",
        DownloadTab.Modpacks => "整合包",
        DownloadTab.ResourcePacks => "资源包",
        DownloadTab.Shaders => "光影",
        _ => "游戏版本"
    };

    public string TabDescription => CurrentTab switch
    {
        DownloadTab.Mods => "搜索并安装模组到当前版本。",
        DownloadTab.Modpacks => "搜索并安装整合包，会自动创建对应版本。",
        DownloadTab.ResourcePacks => "搜索资源包并安装到当前版本。",
        DownloadTab.Shaders => "搜索光影并安装到当前版本。",
        _ => "从 Mojang 官方源下载并安装游戏版本，也可以为当前版本安装 Fabric / Quilt。"
    };

    public string SearchPlaceholder => CurrentTab switch
    {
        DownloadTab.Mods => "搜索模组名称、简介或标签…",
        DownloadTab.Modpacks => "搜索整合包…",
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
                OpenVersionPickerCommand.RaiseCanExecuteChanged();
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

    public DownloadFilterOption? SelectedSort
    {
        get => _selectedSort;
        set
        {
            if (!SetProperty(ref _selectedSort, value) || value == null)
            {
                return;
            }

            SelectSingle(SortFilters, value);
            if (CurrentTab != DownloadTab.Game)
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

    public DownloadFilterOption? SelectedSearchGameVersion
    {
        get => _selectedSearchGameVersion;
        set
        {
            if (!SetProperty(ref _selectedSearchGameVersion, value) || value == null)
            {
                return;
            }

            SelectSingle(SearchGameVersionFilters, value);
            OnPropertyChanged(nameof(FilterSummary));
            if (CurrentTab != DownloadTab.Game)
            {
                _ = SearchAsync();
            }
        }
    }

    public DownloadFilterOption? SelectedCategory
    {
        get => _selectedCategory;        set
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

    public GameInstance? SelectedTargetInstance
    {
        get => _selectedTargetInstance;
        set
        {
            if (!SetProperty(ref _selectedTargetInstance, value))
            {
                return;
            }

            _contextInstance = value;
            TargetMinecraftVersion = value?.MinecraftVersion ?? "";
            OnPropertyChanged(nameof(CurrentInstanceText));
            OnPropertyChanged(nameof(FilterSummary));
            InstallProjectCommand.RaiseCanExecuteChanged();
                OpenVersionPickerCommand.RaiseCanExecuteChanged();
            InstallFabricCommand.RaiseCanExecuteChanged();
            InstallQuiltCommand.RaiseCanExecuteChanged();
            InstallForgeCommand.RaiseCanExecuteChanged();
            InstallNeoForgeCommand.RaiseCanExecuteChanged();
            if (CurrentTab != DownloadTab.Game)
            {
                _ = SearchAsync();
            }
        }
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
                : SelectedSearchGameVersion?.Title ?? "当前目标版本";
            var suffix = CurrentTab == DownloadTab.Mods
                ? $" · {SelectedLoader?.Title ?? "当前加载器"}"
                : "";
            return CurrentTab == DownloadTab.Game
                ? $"目标：Minecraft {_contextInstance.MinecraftVersion} · {loader} · {scope}{suffix}"
                : $"安装目标：Minecraft {_contextInstance.MinecraftVersion} · {loader} · 搜索版本：{scope}{suffix}";
        }
    }

    public bool HasSearchResults => SearchResults.Count > 0;
    public bool ShowSearchEmpty => !IsSearching && !HasSearchResults;

    public void RefreshContext()
    {
        var preferredId = _selectedTargetInstance?.Id ?? _selectedInstance()?.Id;
        TargetInstances.Clear();
        foreach (var instance in _allInstances())
        {
            TargetInstances.Add(instance);
        }

        _selectedTargetInstance = TargetInstances.FirstOrDefault(instance => instance.Id == preferredId)
                                  ?? TargetInstances.FirstOrDefault();
        _contextInstance = _selectedTargetInstance;
        TargetMinecraftVersion = _contextInstance?.MinecraftVersion ?? "";
        OnPropertyChanged(nameof(SelectedTargetInstance));
        OnPropertyChanged(nameof(CurrentInstanceText));
        OnPropertyChanged(nameof(FilterSummary));
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowSearchEmpty));
        InstallFabricCommand.RaiseCanExecuteChanged();
        InstallQuiltCommand.RaiseCanExecuteChanged();
        InstallForgeCommand.RaiseCanExecuteChanged();
        InstallNeoForgeCommand.RaiseCanExecuteChanged();
        InstallProjectCommand.RaiseCanExecuteChanged();
                OpenVersionPickerCommand.RaiseCanExecuteChanged();
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
            RebuildSearchGameVersions();
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

    private void RebuildSearchGameVersions()
    {
        var selectedKey = SelectedSearchGameVersion?.Key ?? "current";
        var versions = _allVersions
            .Where(version => version.Type == "release")
            .Select(version => version.Id)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        SearchGameVersionFilters.Clear();
        SearchGameVersionFilters.Add(new DownloadFilterOption
        {
            Key = "current", Title = "当前目标版本", IsSelected = selectedKey == "current"
        });
        SearchGameVersionFilters.Add(new DownloadFilterOption
        {
            Key = "all", Title = "全部版本", IsSelected = selectedKey == "all"
        });
        foreach (var id in versions)
        {
            SearchGameVersionFilters.Add(new DownloadFilterOption
            {
                Key = id, Title = id, IsSelected = string.Equals(selectedKey, id, StringComparison.OrdinalIgnoreCase)
            });
        }

        _selectedSearchGameVersion = SearchGameVersionFilters.FirstOrDefault(option => option.IsSelected)
                                     ?? SearchGameVersionFilters[0];
        OnPropertyChanged(nameof(SelectedSearchGameVersion));
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

            foreach (var value in SortNewestFirst(values.Distinct(StringComparer.OrdinalIgnoreCase)))
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

    private static IReadOnlyList<string> SortNewestFirst(IEnumerable<string> values)
        => values.OrderByDescending(value => value, LoaderVersionComparer.Instance).ToList();

    private static bool HasCjk(string value)
        => value.Any(ch => ch is >= '\u3400' and <= '\u9fff');

    private sealed class LoaderVersionComparer : IComparer<string>
    {
        public static readonly LoaderVersionComparer Instance = new();
        private static readonly Regex NumericPrefix = new(@"^\d+(?:\.\d+)*", RegexOptions.Compiled);

        public int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x == null)
            {
                return -1;
            }

            if (y == null)
            {
                return 1;
            }

            var left = Parse(x);
            var right = Parse(y);
            var length = Math.Max(left.Numbers.Length, right.Numbers.Length);
            for (var i = 0; i < length; i++)
            {
                var a = i < left.Numbers.Length ? left.Numbers[i] : 0;
                var b = i < right.Numbers.Length ? right.Numbers[i] : 0;
                if (a != b)
                {
                    return a.CompareTo(b);
                }
            }

            if (left.Suffix.Length == 0 && right.Suffix.Length > 0)
            {
                return 1;
            }

            if (left.Suffix.Length > 0 && right.Suffix.Length == 0)
            {
                return -1;
            }

            return string.Compare(left.Suffix, right.Suffix, StringComparison.OrdinalIgnoreCase);
        }

        private static (int[] Numbers, string Suffix) Parse(string value)
        {
            var match = NumericPrefix.Match(value);
            var numbers = match.Success
                ? match.Value.Split('.').Select(part => int.TryParse(part, out var number) ? number : 0).ToArray()
                : Array.Empty<int>();
            var suffix = match.Success ? value[match.Length..] : value;
            return (numbers, suffix);
        }
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
            DownloadTab.Modpacks => new (string Key, string Title)[]
            {
                ("all", "全部类型"), ("adventure", "冒险"), ("technology", "科技"),
                ("magic", "魔法"), ("optimization", "优化"), ("kitchen-sink", "大型整合")
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
            if (resetPage)
            {
                var query = SearchQuery.Trim();
                if (query.Length > 0 && HasCjk(query))
                {
                    SearchStatus = "正在匹配中文名…";
                    var alias = await _mcmod.FindEnglishAliasAsync(query);
                    _effectiveSearchQuery = string.IsNullOrWhiteSpace(alias) ? query : alias;
                }
                else
                {
                    _effectiveSearchQuery = query;
                }
            }

            var loader = CurrentTab == DownloadTab.Mods ? ResolveLoader() : null;
            var gameVersion = SelectedSearchGameVersion?.Key switch
            {
                null or "current" => _contextInstance?.MinecraftVersion,
                "all" => null,
                var version => version
            };
            var category = SelectedCategory?.Key is null or "all" ? null : SelectedCategory.Key;
            var sortIndex = SelectedSort?.Key ?? "relevance";
            var cacheKey = $"{CurrentTab}|{ProjectType}|{_effectiveSearchQuery}|{gameVersion}|{loader}|{category}|{sortIndex}|{_searchOffset}";
            if (!resetPage && _searchCache.TryGetValue(cacheKey, out var cachedPage))
            {
                ReplaceSearchResults(cachedPage.Projects);
                _searchOffset = cachedPage.Offset;
                _searchTotal = cachedPage.Total;
                NotifySearchPageChanged();
                SearchStatus = cachedPage.Projects.Count == 0
                    ? "没有找到匹配的项目，试试换一个关键词。"
                    : $"找到 {cachedPage.Total} 个项目";
                if (CurrentTab == DownloadTab.Mods && !cachedPage.Enriched)
                {
                    _ = EnrichMcmodAsync(cachedPage.Projects, searchVersion, cacheKey);
                }

                return;
            }

            var result = await _modrinth.SearchAsync(_effectiveSearchQuery, ProjectType,
                gameVersion, loader, category, sortIndex, _searchOffset, SearchPageSize);
            _searchOffset = result.Offset;
            _searchTotal = result.TotalHits;
            ReplaceSearchResults(result.Hits);
            RememberSearchPage(cacheKey, result.Hits, result.Offset, result.TotalHits);
            NotifySearchPageChanged();
            SearchStatus = result.Hits.Count == 0
                ? "没有找到匹配的项目，试试换一个关键词。"
                : CurrentTab == DownloadTab.Mods
                    ? $"找到 {result.TotalHits} 个项目 · 正在匹配 MC百科…"
                    : $"找到 {result.TotalHits} 个项目";
            if (CurrentTab == DownloadTab.Mods && result.Hits.Count > 0)
            {
                _ = EnrichMcmodAsync(result.Hits, searchVersion, cacheKey);
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
                OpenVersionPickerCommand.RaiseCanExecuteChanged();
            PreviousPageCommand.RaiseCanExecuteChanged();
            NextPageCommand.RaiseCanExecuteChanged();
        }
    }

    private void ReplaceSearchResults(IEnumerable<ModrinthProject> projects)
    {
        SearchResults.Clear();
        foreach (var project in projects)
        {
            SearchResults.Add(project);
        }
    }

    private void NotifySearchPageChanged()
    {
        OnPropertyChanged(nameof(HasSearchResults));
        OnPropertyChanged(nameof(ShowSearchEmpty));
        OnPropertyChanged(nameof(SearchPage));
        OnPropertyChanged(nameof(SearchTotalPages));
        OnPropertyChanged(nameof(SearchPageText));
    }

    private void RememberSearchPage(string key, IReadOnlyList<ModrinthProject> projects, int offset, int total)
    {
        if (_searchCache.ContainsKey(key))
        {
            return;
        }

        _searchCache[key] = new CachedSearchPage(projects.ToList(), offset, total, false);
        _searchCacheOrder.Enqueue(key);
        while (_searchCacheOrder.Count > 20)
        {
            var oldest = _searchCacheOrder.Dequeue();
            _searchCache.Remove(oldest);
        }

        SaveSearchCacheToDisk();
    }

    private static string SearchCacheFilePath
        => Path.Combine(AppPaths.CacheDirectory, "search-pages.json");

    private void LoadSearchCacheFromDisk()
    {
        try
        {
            if (!File.Exists(SearchCacheFilePath))
            {
                return;
            }

            var document = JsonSerializer.Deserialize<SearchCacheDocument>(
                File.ReadAllText(SearchCacheFilePath));
            if (document?.Pages == null)
            {
                return;
            }

            var cutoff = DateTime.UtcNow.AddHours(-24);
            foreach (var pair in document.Pages)
            {
                if (pair.Value?.Projects == null || pair.Value.SavedAt < cutoff)
                {
                    continue;
                }

                _searchCache[pair.Key] = new CachedSearchPage(
                    pair.Value.Projects, pair.Value.Offset, pair.Value.Total, pair.Value.Enriched);
                _searchCacheOrder.Enqueue(pair.Key);
            }
        }
        catch
        {
            // A damaged or outdated disk cache is ignored.
        }
    }

    private void SaveSearchCacheToDisk()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.CacheDirectory);
            var document = new SearchCacheDocument();
            foreach (var pair in _searchCache)
            {
                document.Pages[pair.Key] = new SearchCacheEntryDocument
                {
                    Projects = pair.Value.Projects,
                    Offset = pair.Value.Offset,
                    Total = pair.Value.Total,
                    Enriched = pair.Value.Enriched,
                    SavedAt = DateTime.UtcNow
                };
            }

            File.WriteAllText(SearchCacheFilePath,
                JsonSerializer.Serialize(document, new JsonSerializerOptions { WriteIndented = false }));
        }
        catch
        {
            // Cache persistence is best-effort.
        }
    }

    public sealed class SearchCacheDocument
    {
        public Dictionary<string, SearchCacheEntryDocument> Pages { get; set; } = new();
    }

    public sealed class SearchCacheEntryDocument
    {
        public List<ModrinthProject> Projects { get; set; } = new();
        public int Offset { get; set; }
        public int Total { get; set; }
        public bool Enriched { get; set; }
        public DateTime SavedAt { get; set; }
    }

    private sealed record CachedSearchPage(List<ModrinthProject> Projects, int Offset, int Total, bool Enriched);

    private async Task EnrichMcmodAsync(IReadOnlyList<ModrinthProject> hits, int searchVersion, string cacheKey)
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

            SearchResults[index] = project with
            {
                ChineseName = HasCjk(info.Name) ? info.Name : null,
                McmodUrl = info.Url
            };

            if (_searchCache.TryGetValue(cacheKey, out var cachedPage))
            {
                var cachedIndex = cachedPage.Projects.FindIndex(item => item.Id == project.Id);
                if (cachedIndex >= 0)
                {
                    cachedPage.Projects[cachedIndex] = SearchResults[index];
                }
            }
        }

        if (searchVersion == _searchVersion)
        {
            SearchStatus = $"找到 {_searchTotal} 个项目";
            if (_searchCache.TryGetValue(cacheKey, out var page))
            {
                _searchCache[cacheKey] = page with { Enriched = true };
                SaveSearchCacheToDisk();
            }
        }
    }

    private async Task<IReadOnlyList<ModDependencyItem>> AnalyzeDependenciesAsync(ModrinthVersion version,
        string gameVersion, string? loader, CancellationToken cancellationToken)
    {
        var instance = ResolveInstallInstance(gameVersion, loader);
        if (instance == null)
        {
            return Array.Empty<ModDependencyItem>();
        }

        var installed = await new ModService().LoadModsAsync(instance, cancellationToken);
        var result = new List<ModDependencyItem>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versionCache = new Dictionary<string, ModrinthVersion?>(StringComparer.OrdinalIgnoreCase);
        var projectCache = new Dictionary<string, ModrinthProject?>(StringComparer.OrdinalIgnoreCase);

        async Task WalkAsync(ModrinthVersion current, int depth)
        {
            foreach (var dependency in current.Dependencies)
            {
                if (dependency.DependencyType is not ("required" or "optional"))
                {
                    continue;
                }

                var key = DependencyKey(dependency);
                if (key.Length == 0 || !visited.Add(key))
                {
                    continue;
                }

                ModrinthProject? project = null;
                ModrinthVersion? dependencyVersion = null;
                try
                {
                    (project, dependencyVersion) = await ResolveDependencyAsync(
                        dependency, gameVersion, loader, projectCache, versionCache, cancellationToken);
                }
                catch
                {
                    // Keep an unresolved marker below; other prerequisites
                    // must still be shown.
                }

                result.Add(new ModDependencyItem
                {
                    ProjectId = project?.Id ?? dependency.ProjectId ?? "",
                    VersionId = dependency.VersionId,
                    Title = project?.Title ??
                            (!string.IsNullOrWhiteSpace(dependency.FileName) ? dependency.FileName! : "未知前置"),
                    DependencyType = dependency.DependencyType,
                    Installed = project != null && IsDependencyInstalled(project, installed),
                    Resolved = project != null,
                    Depth = depth
                });

                var installedHere = result[^1].Installed;
                if (dependencyVersion != null &&
                    (dependency.DependencyType == "required" || installedHere))
                {
                    await WalkAsync(dependencyVersion, depth + 1);
                }
            }
        }

        await WalkAsync(version, 0);
        return result;
    }

    private async Task<IReadOnlyList<ModDependencyItem>> AnalyzeJarDependenciesAsync(ModrinthVersion version,
        string gameVersion, string? loader, CancellationToken cancellationToken)
    {
        var instance = ResolveInstallInstance(gameVersion, loader);
        if (instance == null || version.File == null)
        {
            return Array.Empty<ModDependencyItem>();
        }

        var installed = await new ModService().LoadModsAsync(instance, cancellationToken);
        var result = new List<ModDependencyItem>();
        await AddJarDependenciesAsync(version, gameVersion, loader, installed, result, cancellationToken);
        return result;
    }

    private async Task AddJarDependenciesAsync(ModrinthVersion version, string gameVersion, string? loader,
        IReadOnlyList<ModInfo> installed, List<ModDependencyItem> result, CancellationToken cancellationToken)
    {
        if (version.File == null)
        {
            return;
        }

        IReadOnlyList<JarDependency> jarDependencies;
        if (_jarDependencyCache.TryGetValue(version.Id, out var cached))
        {
            jarDependencies = cached;
        }
        else
        {
            jarDependencies = await LoadJarDependenciesAsync(version, cancellationToken);
            _jarDependencyCache[version.Id] = jarDependencies;
        }

        foreach (var dependency in jarDependencies)
        {
            if (result.Any(item =>
                    string.Equals(item.Title, dependency.ModId, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizeDependencyKey(item.Title), NormalizeDependencyKey(dependency.ModId),
                        StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            var project = await ResolveModIdProjectAsync(dependency.ModId, gameVersion, loader, cancellationToken);
            result.Add(new ModDependencyItem
            {
                ProjectId = project?.Id ?? dependency.ModId,
                Title = project?.Title ?? dependency.ModId,
                DependencyType = dependency.Mandatory ? "required" : "optional",
                Installed = project != null && IsDependencyInstalled(project, installed),
                Resolved = project != null,
                Depth = 0
            });
        }
    }

    private async Task<IReadOnlyList<JarDependency>> LoadJarDependenciesAsync(ModrinthVersion version,
        CancellationToken cancellationToken)
    {
        if (version.File == null)
        {
            return Array.Empty<JarDependency>();
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            return await ModJarDependencyReader.ReadFromUrlAsync(
                version.File.Url, version.File.Size, timeout.Token);
        }
        catch
        {
            return Array.Empty<JarDependency>();
        }
    }

    private async Task<ModrinthProject?> ResolveModIdProjectAsync(string modId, string gameVersion, string? loader,
        CancellationToken cancellationToken)
    {
        try
        {
            var search = await _modrinth.SearchAsync(modId, "mod", gameVersion, loader, index: "relevance",
                offset: 0, limit: 5, cancellationToken: cancellationToken);
            var key = NormalizeDependencyKey(modId);
            return search.Hits.FirstOrDefault(project =>
                       NormalizeDependencyKey(project.Slug) == key ||
                       NormalizeDependencyKey(project.Title) == key) ??
                   search.Hits.FirstOrDefault(project =>
                       NormalizeDependencyKey(project.Slug).Contains(key, StringComparison.Ordinal) ||
                       NormalizeDependencyKey(project.Title).Contains(key, StringComparison.Ordinal)) ??
                   search.Hits.FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task InstallMissingDependenciesAsync(ModrinthVersion version, string gameVersion, string? loader,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var instance = ResolveInstallInstance(gameVersion, loader);
        if (instance == null)
        {
            throw new InvalidOperationException("找不到对应的已安装版本。");
        }

        var installed = await new ModService().LoadModsAsync(instance, cancellationToken);
        var installedProjectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mod in installed)
        {
            installedProjectIds.Add(NormalizeDependencyKey(mod.ModId));
            installedProjectIds.Add(NormalizeDependencyKey(mod.Name));
            installedProjectIds.Add(NormalizeDependencyKey(mod.FileName));
        }

        var visiting = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var versionCache = new Dictionary<string, ModrinthVersion?>(StringComparer.OrdinalIgnoreCase);
        var projectCache = new Dictionary<string, ModrinthProject?>(StringComparer.OrdinalIgnoreCase);

        async Task InstallAsync(ModrinthVersion current)
        {
            foreach (var dependency in current.Dependencies.Where(item => item.DependencyType == "required"))
            {
                var key = DependencyKey(dependency);
                if (key.Length == 0 || !visiting.Add(key))
                {
                    continue;
                }

                ModrinthProject? project;
                ModrinthVersion? dependencyVersion;
                try
                {
                    (project, dependencyVersion) = await ResolveDependencyAsync(
                        dependency, gameVersion, loader, projectCache, versionCache, cancellationToken);
                }
                catch
                {
                    continue;
                }

                if (project == null)
                {
                    continue;
                }

                var alreadyInstalled = IsDependencyInstalled(project, installed) ||
                                       installedProjectIds.Contains(NormalizeDependencyKey(project.Slug));
                if (dependencyVersion?.File != null && !alreadyInstalled)
                {
                    progress?.Invoke(new DownloadProgress
                    {
                        Phase = "前置",
                        Message = $"安装前置 {project.Title}…",
                        Percent = -1
                    });
                    await _modrinth.InstallVersionAsync(project, dependencyVersion, instance.ModsDirectory, progress, cancellationToken);
                    installedProjectIds.Add(NormalizeDependencyKey(project.Slug));
                    installedProjectIds.Add(NormalizeDependencyKey(project.Title));
                }

                if (dependencyVersion != null)
                {
                    await InstallAsync(dependencyVersion);
                }
            }
        }

        await InstallAsync(version);

        if (CurrentTab == DownloadTab.Mods && version.File != null)
        {
            var jarDependencies = await LoadJarDependenciesAsync(version, cancellationToken);
            foreach (var dependency in jarDependencies.Where(item => item.Mandatory))
            {
                var project = await ResolveModIdProjectAsync(dependency.ModId, gameVersion, loader, cancellationToken);
                if (project == null || IsDependencyInstalled(project, installed) ||
                    installedProjectIds.Contains(NormalizeDependencyKey(project.Slug)))
                {
                    continue;
                }

                var versions = await _modrinth.GetVersionsAsync(project.Id, gameVersion, loader, cancellationToken);
                var dependencyVersion = versions.FirstOrDefault();
                if (dependencyVersion?.File == null)
                {
                    continue;
                }

                progress?.Invoke(new DownloadProgress
                {
                    Phase = "前置",
                    Message = $"安装前置 {project.Title}…",
                    Percent = -1
                });
                await _modrinth.InstallVersionAsync(project, dependencyVersion, instance.ModsDirectory, progress, cancellationToken);
                installedProjectIds.Add(NormalizeDependencyKey(project.Slug));
                installedProjectIds.Add(NormalizeDependencyKey(project.Title));
            }
        }
    }

    private static string DependencyKey(ModrinthDependency dependency)
        => !string.IsNullOrWhiteSpace(dependency.ProjectId)
            ? "project:" + dependency.ProjectId
            : !string.IsNullOrWhiteSpace(dependency.VersionId)
                ? "version:" + dependency.VersionId
                : "";

    private async Task<(ModrinthProject? Project, ModrinthVersion? Version)> ResolveDependencyAsync(
        ModrinthDependency dependency, string gameVersion, string? loader,
        Dictionary<string, ModrinthProject?> projectCache,
        Dictionary<string, ModrinthVersion?> versionCache,
        CancellationToken cancellationToken)
    {
        ModrinthVersion? exactVersion = null;
        if (!string.IsNullOrWhiteSpace(dependency.VersionId))
        {
            if (!versionCache.TryGetValue(dependency.VersionId, out exactVersion))
            {
                exactVersion = await _modrinth.GetVersionAsync(dependency.VersionId, cancellationToken);
                versionCache[dependency.VersionId] = exactVersion;
            }
        }

        var projectId = dependency.ProjectId ?? exactVersion?.ProjectId;
        if (string.IsNullOrWhiteSpace(projectId))
        {
            return (null, exactVersion);
        }

        if (!projectCache.TryGetValue(projectId, out var project))
        {
            project = await _modrinth.GetProjectAsync(projectId, cancellationToken);
            projectCache[projectId] = project;
        }

        var version = exactVersion;
        if (version == null && project != null)
        {
            var versions = await _modrinth.GetVersionsAsync(project.Id, gameVersion, loader, cancellationToken);
            version = versions.FirstOrDefault();
        }

        return (project, version);
    }

    private static bool IsDependencyInstalled(ModrinthProject project, IReadOnlyList<ModInfo> installed)
    {
        var projectKeys = new[]
        {
            NormalizeDependencyKey(project.Slug),
            NormalizeDependencyKey(project.Title)
        }.Where(key => key.Length >= 4).Distinct().ToArray();
        if (projectKeys.Length == 0)
        {
            return false;
        }

        foreach (var mod in installed.Where(mod => mod.Enabled))
        {
            var modKeys = new[]
            {
                NormalizeDependencyKey(mod.ModId),
                NormalizeDependencyKey(mod.Name),
                NormalizeDependencyKey(mod.FileName),
                NormalizeDependencyKey(Path.GetFileNameWithoutExtension(mod.FileName))
            }.Where(key => key.Length >= 4);

            foreach (var projectKey in projectKeys)
            {
                foreach (var modKey in modKeys)
                {
                    if (modKey == projectKey ||
                        modKey.Contains(projectKey, StringComparison.Ordinal) ||
                        projectKey.Contains(modKey, StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    private static string NormalizeDependencyKey(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? ""
            : new string(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private async Task OpenModpackPickerAsync(ModrinthProject project)
    {
        var root = _root();
        if (string.IsNullOrWhiteSpace(root))
        {
            _toast("没有找到 .minecraft 目录。", true);
            return;
        }

        var defaultVersion = SelectedSearchGameVersion?.Key switch
        {
            null or "current" or "all" => _contextInstance?.MinecraftVersion ??
                                         _allVersions.FirstOrDefault(version => version.Type == "release")?.Id,
            var version => version
        };
        if (string.IsNullOrWhiteSpace(defaultVersion))
        {
            _toast("没有可用的游戏版本。", true);
            return;
        }

        var gameVersions = SearchGameVersionFilters
            .Where(option => option.Key is not (null or "current" or "all"))
            .Select(option => option.Key)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!gameVersions.Contains(defaultVersion, StringComparer.OrdinalIgnoreCase))
        {
            gameVersions.Insert(0, defaultVersion);
        }

        var window = new ModVersionWindow(project, defaultVersion, gameVersions, null,
            Array.Empty<string>(),
            async (version, _) => (IReadOnlyList<ModrinthVersion>)await _modrinth.GetVersionsAsync(project.Id, version, null),
            (_, _, _) => Task.FromResult<IReadOnlyList<ModDependencyItem>>(Array.Empty<ModDependencyItem>()),
            (_, _, _) => Task.FromResult<IReadOnlyList<ModDependencyItem>>(Array.Empty<ModDependencyItem>()),
            (_, _, _, _) => Task.CompletedTask)
        {
            Owner = Application.Current.MainWindow,
            Title = "安装整合包"
        };
        if (window.ShowDialog() != true || window.SelectedVersion?.File == null)
        {
            return;
        }

        var selected = window.SelectedVersion;
        var directory = Path.Combine(AppPaths.CacheDirectory, "modpacks");
        Directory.CreateDirectory(directory);
        var safeName = string.Join("_", project.Title.Split(Path.GetInvalidFileNameChars(),
            StringSplitOptions.RemoveEmptyEntries));
        var archivePath = Path.Combine(directory, $"{safeName}-{selected.VersionNumber}.mrpack");
        await RunDownloadAsync($"安装整合包 {project.DisplayTitle} {selected.VersionNumber}", async (report, token) =>
        {
            await DownloadHelper.DownloadFileAsync(selected.File.Url, archivePath, null, token);
            var id = await _modpacks.InstallAsync(archivePath, root, report, token);
            await _reloadInstances();
            _toast($"整合包已安装为 {id}。", false);
        });
    }

    private async Task OpenVersionPickerAsync(ModrinthProject project)
    {
        if (CurrentTab == DownloadTab.Modpacks)
        {
            await OpenModpackPickerAsync(project);
            return;
        }

        var target = _contextInstance;
        if (target == null)
        {
            _toast("请先选择一个游戏版本。", true);
            return;
        }

        var defaultVersion = SelectedSearchGameVersion?.Key switch
        {
            null or "current" or "all" => target.MinecraftVersion,
            var version => version
        };
        var gameVersions = SearchGameVersionFilters
            .Where(option => option.Key is not (null or "current" or "all"))
            .Select(option => option.Key)
            .Where(version => !string.IsNullOrWhiteSpace(version))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (!gameVersions.Contains(defaultVersion, StringComparer.OrdinalIgnoreCase))
        {
            gameVersions.Insert(0, defaultVersion);
        }

        var loaders = CurrentTab == DownloadTab.Mods
            ? new List<string> { "Forge", "NeoForge", "Fabric", "Quilt" }
            : new List<string>();
        var filterLoader = SelectedLoader?.Key;
        var defaultLoaderKey = CurrentTab == DownloadTab.Mods
            ? filterLoader is null or "current" or "all"
                ? NormalizeLoader(target.Loader)
                : filterLoader
            : null;
        var defaultLoaderTitle = LoaderTitle(defaultLoaderKey) ?? (loaders.Count > 0 ? loaders[0] : null);
        if (defaultLoaderTitle != null && !loaders.Contains(defaultLoaderTitle, StringComparer.OrdinalIgnoreCase))
        {
            loaders.Insert(0, defaultLoaderTitle);
        }

        ModVersionWindow window;
        try
        {
            window = new ModVersionWindow(project, defaultVersion, gameVersions, defaultLoaderTitle, loaders,
                async (version, selectedLoader) => (IReadOnlyList<ModrinthVersion>)await _modrinth.GetVersionsAsync(
                    project.Id, version, NormalizeLoader(selectedLoader)),
                (version, gameVersion, selectedLoader) => AnalyzeDependenciesAsync(
                    version, gameVersion, NormalizeLoader(selectedLoader), CancellationToken.None),
                (version, gameVersion, selectedLoader) => AnalyzeJarDependenciesAsync(
                    version, gameVersion, NormalizeLoader(selectedLoader), CancellationToken.None),
                (version, gameVersion, selectedLoader, progress) => InstallMissingDependenciesAsync(
                    version, gameVersion, NormalizeLoader(selectedLoader), progress, CancellationToken.None))
            {
                Owner = Application.Current.MainWindow
            };
        }
        catch (Exception ex)
        {
            _toast($"无法打开安装界面：{ex.Message}", true);
            return;
        }

        if (window.ShowDialog() != true || window.SelectedVersion?.File == null)
        {
            return;
        }
        var selected = window.SelectedVersion;
        var selectedGameVersion = window.SelectedGameVersion;
        var selectedLoaderKey = NormalizeLoader(window.SelectedLoader) ?? defaultLoaderKey;
        var installInstance = ResolveInstallInstance(selectedGameVersion, selectedLoaderKey);
        if (installInstance == null)
        {
            var loaderText = LoaderTitle(selectedLoaderKey);
            _toast($"没有已安装的 Minecraft {selectedGameVersion}{(loaderText == null ? "" : $" + {loaderText}")} 版本，无法安装该文件。", true);
            return;
        }

        var destination = CurrentTab switch
        {
            DownloadTab.ResourcePacks => Path.Combine(installInstance.GameDirectory, "resourcepacks"),
            DownloadTab.Shaders => Path.Combine(installInstance.GameDirectory, "shaderpacks"),
            _ => installInstance.ModsDirectory
        };
        await RunDownloadAsync($"安装 {project.DisplayTitle} {selected.VersionNumber}", async (report, token) =>
        {
            var path = await _modrinth.InstallVersionAsync(project, selected, destination, report, token);
            _toast($"已安装到 {installInstance.Id}：{Path.GetFileName(path)}。", false);
        });
    }

    private GameInstance? ResolveInstallInstance(string? gameVersion, string? loader)
    {
        if (string.IsNullOrWhiteSpace(gameVersion))
        {
            return _contextInstance;
        }

        bool LoaderMatches(GameInstance instance)
            => loader == null ||
               string.Equals(NormalizeLoader(instance.Loader), loader, StringComparison.OrdinalIgnoreCase);

        if (_contextInstance != null &&
            string.Equals(_contextInstance.MinecraftVersion, gameVersion, StringComparison.OrdinalIgnoreCase) &&
            LoaderMatches(_contextInstance))
        {
            return _contextInstance;
        }

        return _allInstances().FirstOrDefault(instance =>
            string.Equals(instance.MinecraftVersion, gameVersion, StringComparison.OrdinalIgnoreCase) &&
            LoaderMatches(instance));
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

    private static string? LoaderTitle(string? loader) => loader switch
    {
        "fabric" => "Fabric",
        "forge" => "Forge",
        "neoforge" => "NeoForge",
        "quilt" => "Quilt",
        _ => null
    };
}










