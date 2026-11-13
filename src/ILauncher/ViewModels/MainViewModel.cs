using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.Localization;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _toastTimer;
    private object? _currentPage;
    private GameInstance? _selectedInstance;
    private string _instanceSearch = "";
    private DownloadFilterOption? _selectedHomeLoader;
    private DownloadFilterOption? _selectedHomeType;
    private DownloadFilterOption? _selectedHomeLayout;
    private int _homeVersionsPerRow = 3;
    private string _rootPath = "";
    private string _selectedJavaText = "检测中…";
    private JavaOption? _selectedHomeJava;
    private bool _homeJavaLoading;
    private bool _isBusy;
    private string _busyMessage = "正在扫描游戏版本…";
    private bool _isHome = true;
    private bool _isSettingsPage;
    private bool _isAccountPage;
    private bool _isDownloadPage;
    private bool _isInstancePage;
    private DownloadViewModel? _downloadPage;
    private SettingsViewModel? _settingsPage;
    private string _headerTitle = "";
    private string _headerSubtitle = "";
    private string _toastMessage = "";
    private bool _toastVisible;
    private bool _toastIsError;
    private bool _suppressSelectionSave;

    public MainViewModel()
    {
        SettingsService = new SettingsService();
        LanguageService.SetLanguage(SettingsService.Settings.UiLanguage);
        if (string.Equals(SettingsService.Settings.AccountType, "微软账户", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(SettingsService.Settings.MicrosoftAccessTokenProtected))
        {
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(SettingsService.Settings.MicrosoftAccessTokenProtected), null,
                    DataProtectionScope.CurrentUser);
                SettingsService.Settings.MicrosoftAccessToken = Encoding.UTF8.GetString(bytes);
                SettingsService.Settings.MicrosoftRefreshToken =
                    TokenProtection.Unprotect(SettingsService.Settings.MicrosoftRefreshTokenProtected);
            }
            catch
            {
                // An old/corrupted token simply falls back to offline mode.
            }
        }

        InstallationService = new InstallationService(SettingsService);
        JavaService = new JavaService(SettingsService);
        ModService = new ModService();
        MojangGameService = new MojangGameService();
        FabricService = new FabricService();
        QuiltService = new QuiltService();
        ModrinthService = new ModrinthService();
        ModLoaderInstallerService = new ModLoaderInstallerService(JavaService, SettingsService);
        ModpackService = new ModpackService(MojangGameService, FabricService, QuiltService, ModLoaderInstallerService);
        LaunchService = new LaunchService(JavaService);
        LaunchLog = new LaunchLogViewModel(LaunchService, SettingsService, Toast);

        _toastTimer = new DispatcherTimer(DispatcherPriority.Normal) { Interval = TimeSpan.FromSeconds(3.4) };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastVisible = false;
        };

        Instances = new ObservableCollection<GameInstance>();
        FilteredInstances = new ObservableCollection<GameInstance>();
        JavaRuntimes = new ObservableCollection<JavaInfo>();
        HomeLoaderFilters = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "all", Title = "全部", IsSelected = true },
            new() { Key = "vanilla", Title = "原版" },
            new() { Key = "forge", Title = "Forge" },
            new() { Key = "fabric", Title = "Fabric" },
            new() { Key = "neoforge", Title = "NeoForge" },
            new() { Key = "quilt", Title = "Quilt" },
            new() { Key = "optifine", Title = "OptiFine" }
        };
        HomeTypeFilters = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "all", Title = "全部", IsSelected = true },
            new() { Key = "release", Title = "正式版" },
            new() { Key = "snapshot", Title = "快照" },
            new() { Key = "old", Title = "旧版" }
        };
        _selectedHomeLoader = HomeLoaderFilters[0];
        _selectedHomeType = HomeTypeFilters[0];
        HomeLayoutOptions = new ObservableCollection<DownloadFilterOption>
        {
            new() { Key = "1", Title = "列表 · 1 个/行" },
            new() { Key = "2", Title = "2 个/行" },
            new() { Key = "3", Title = "3 个/行", IsSelected = true },
            new() { Key = "4", Title = "4 个/行" }
        };
        _homeVersionsPerRow = Math.Clamp(SettingsService.Settings.HomeVersionsPerRow, 1, 4);
        _selectedHomeLayout = HomeLayoutOptions.First(option => option.Key == _homeVersionsPerRow.ToString());
        SelectSingleFilter(HomeLayoutOptions, _selectedHomeLayout);

        _currentPage = this;
        GoHomeCommand = new RelayCommand(GoHome);
        GoSettingsCommand = new RelayCommand(GoSettings);
        GoAccountCommand = new RelayCommand(GoAccount);
        GoDownloadCommand = new RelayCommand(GoDownload);
        OpenSettingsCommand = new RelayCommand(GoSettings);
        OpenInstanceCommand = new RelayCommand(parameter =>
        {
            var instance = parameter as GameInstance ?? SelectedInstance;
            if (instance != null)
            {
                OpenInstance(instance);
            }
        });
        SelectInstanceCommand = new RelayCommand(parameter =>
        {
            if (parameter is GameInstance instance)
            {
                SelectedInstance = instance;
            }
        });
        LaunchCommand = new RelayCommand(LaunchSelected);
        RefreshCommand = new AsyncRelayCommand(() => ReloadInstancesAsync(true));
        OpenRootFolderCommand = new RelayCommand(OpenRootFolder);
        OpenGameLogCommand = new RelayCommand(OpenGameLog);
        OpenInstanceModsCommand = new RelayCommand(() =>
        {
            if (SelectedInstance != null)
            {
                OpenInstance(SelectedInstance, InstanceSection.Mods);
            }
        });
        OpenInstanceTestCommand = new RelayCommand(() =>
        {
            if (SelectedInstance != null)
            {
                OpenInstance(SelectedInstance);
                if (CurrentPage is InstanceViewModel instancePage)
                {
                    instancePage.TestGameCommand.Execute(null);
                }
            }
        });

        LaunchLog.PropertyChanged += OnLaunchLogPropertyChanged;
        LaunchLog.Logs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TestCardLogText));
    }

    public SettingsService SettingsService { get; }
    public InstallationService InstallationService { get; }
    public JavaService JavaService { get; }
    public ModService ModService { get; }
    public MojangGameService MojangGameService { get; }
    public FabricService FabricService { get; }
    public QuiltService QuiltService { get; }
    public ModrinthService ModrinthService { get; }
    public ModLoaderInstallerService ModLoaderInstallerService { get; }
    public ModpackService ModpackService { get; }
    public LaunchService LaunchService { get; }
    public LaunchLogViewModel LaunchLog { get; }

    public ObservableCollection<GameInstance> Instances { get; }
    public ObservableCollection<GameInstance> FilteredInstances { get; }
    public ObservableCollection<DownloadFilterOption> HomeLoaderFilters { get; }
    public ObservableCollection<DownloadFilterOption> HomeTypeFilters { get; }
    public ObservableCollection<DownloadFilterOption> HomeLayoutOptions { get; }
    public ObservableCollection<JavaInfo> JavaRuntimes { get; }

    /// <summary>运行环境面板里的 Java 下拉选项（含“自动选择”）。</summary>
    public ObservableCollection<JavaOption> HomeJavaOptions { get; } = new();

    public JavaOption? SelectedHomeJava
    {
        get => _selectedHomeJava;
        set
        {
            if (!SetProperty(ref _selectedHomeJava, value) || value == null || _homeJavaLoading)
            {
                return;
            }

            SettingsService.Settings.JavaPath = value.IsAuto ? "" : value.Path;
            SettingsService.Save();
            foreach (var option in HomeJavaOptions)
            {
                option.IsSelected = ReferenceEquals(option, value);
            }

            _ = UpdateSelectedJavaAsync();
        }
    }

    public object? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(IsHome));
                Application.Current?.Dispatcher.BeginInvoke(new Action(LanguageService.Apply),
                    DispatcherPriority.Loaded);
                Application.Current?.Dispatcher.BeginInvoke(new Action(LanguageService.Apply),
                    DispatcherPriority.ApplicationIdle);
            }
        }
    }

    public GameInstance? SelectedInstance
    {
        get => _selectedInstance;
        set
        {
            if (!SetProperty(ref _selectedInstance, value))
            {
                return;
            }

            OnPropertyChanged(nameof(SelectedSubtitle));
            OnPropertyChanged(nameof(SelectedLoader));
            OnPropertyChanged(nameof(SelectedMinecraftVersion));
            OnPropertyChanged(nameof(SelectedModsText));
            OnPropertyChanged(nameof(SelectedTypeText));
            OnPropertyChanged(nameof(HasSelection));
            LaunchCommand.RaiseCanExecuteChanged();
            if (!_suppressSelectionSave && value != null)
            {
                SettingsService.Settings.LastInstance = value.Id;
                SettingsService.Save();
            }

            _downloadPage?.RefreshContext();
            _ = UpdateSelectedJavaAsync();
        }
    }

    public bool HasSelection => SelectedInstance != null;

    public string SelectedSubtitle => SelectedInstance?.Subtitle ?? "";
    public string SelectedLoader => SelectedInstance?.Loader ?? "";
    public string SelectedMinecraftVersion => SelectedInstance?.MinecraftVersion ?? "";
    public string SelectedModsText => SelectedInstance == null
        ? ""
        : LanguageService.CurrentLanguage == LanguageService.English
            ? $"{SelectedInstance.ModCount} {(SelectedInstance.ModCount == 1 ? "mod" : "mods")}"
            : $"{SelectedInstance.ModCount} 个模组";
    public string SelectedTypeText => SelectedInstance?.TypeLabel ?? "";

    public int InstanceCount => Instances.Count;
    public int FilteredInstanceCount => FilteredInstances.Count;
    public string InstanceCountText => FilteredInstanceCount == InstanceCount
        ? InstanceCount.ToString()
        : $"{FilteredInstanceCount} / {InstanceCount}";
    public bool IsHomeFilterEmpty => Instances.Count > 0 && FilteredInstances.Count == 0;

    public string InstanceSearch
    {
        get => _instanceSearch;
        set
        {
            if (SetProperty(ref _instanceSearch, value))
            {
                ApplyInstanceFilter();
            }
        }
    }

    public DownloadFilterOption? SelectedHomeLoader
    {
        get => _selectedHomeLoader;
        set
        {
            if (!SetProperty(ref _selectedHomeLoader, value) || value == null)
            {
                return;
            }

            SelectSingleFilter(HomeLoaderFilters, value);
            ApplyInstanceFilter();
        }
    }

    public DownloadFilterOption? SelectedHomeType
    {
        get => _selectedHomeType;
        set
        {
            if (!SetProperty(ref _selectedHomeType, value) || value == null)
            {
                return;
            }

            SelectSingleFilter(HomeTypeFilters, value);
            ApplyInstanceFilter();
        }
    }

    public DownloadFilterOption? SelectedHomeLayout
    {
        get => _selectedHomeLayout;
        set
        {
            if (!SetProperty(ref _selectedHomeLayout, value) || value == null)
            {
                return;
            }

            SelectSingleFilter(HomeLayoutOptions, value);
            if (int.TryParse(value.Key, out var columns))
            {
                HomeVersionsPerRow = columns;
            }
        }
    }

    public int HomeVersionsPerRow
    {
        get => _homeVersionsPerRow;
        set
        {
            var columns = Math.Clamp(value, 1, 4);
            if (!SetProperty(ref _homeVersionsPerRow, columns))
            {
                return;
            }

            SettingsService.Settings.HomeVersionsPerRow = columns;
            SettingsService.Save();
        }
    }

    public bool IsHome
    {
        get => _isHome;
        private set
        {
            if (SetProperty(ref _isHome, value))
            {
                OnPropertyChanged(nameof(IsHomeOrInstance));
            }
        }
    }

    public bool IsSettingsPage
    {
        get => _isSettingsPage;
        private set => SetProperty(ref _isSettingsPage, value);
    }

    public bool IsAccountPage
    {
        get => _isAccountPage;
        private set => SetProperty(ref _isAccountPage, value);
    }

    public bool IsDownloadPage
    {
        get => _isDownloadPage;
        private set => SetProperty(ref _isDownloadPage, value);
    }

    public bool IsInstancePage
    {
        get => _isInstancePage;
        private set
        {
            if (SetProperty(ref _isInstancePage, value))
            {
                OnPropertyChanged(nameof(IsHomeOrInstance));
            }
        }
    }

    public bool IsHomeOrInstance => IsHome || IsInstancePage;

    public bool ShowBack => !IsHome;

    public string HeaderTitle
    {
        get => _headerTitle;
        private set => SetProperty(ref _headerTitle, value);
    }

    public string HeaderSubtitle
    {
        get => _headerSubtitle;
        private set => SetProperty(ref _headerSubtitle, value);
    }

    public string RootPath
    {
        get => _rootPath;
        private set => SetProperty(ref _rootPath, value);
    }

    public string SelectedJavaText
    {
        get => _selectedJavaText;
        private set => SetProperty(ref _selectedJavaText, value);
    }

    /// <summary>测试游戏卡片上的一行实时日志。</summary>
    public string TestCardLogText
    {
        get
        {
            var last = LaunchLog.Logs.LastOrDefault();
            if (last != null && !string.IsNullOrWhiteSpace(last.DisplayMessage))
            {
                var text = last.DisplayMessage.Length > 64
                    ? last.DisplayMessage[..64] + "…"
                    : last.DisplayMessage;
                return LaunchLog.IsRunning ? $"运行中 · {text}" : $"上次启动 · {text}";
            }

            return LaunchLog.IsRunning ? "正在启动游戏…" : "点击下方按钮启动测试，这里会显示实时日志";
        }
    }

    public string PlayerName => SettingsService.ResolvePlayerName();

    public string PlayerInitial => string.IsNullOrWhiteSpace(PlayerName) ? "P" : PlayerName[..1].ToUpperInvariant();

    public string SkinPath
    {
        get
        {
            var skins = (Application.Current as App)?.Skins;
            return SkinResolver.Resolve(SettingsService, skins);
        }
    }

    public string SkinModel => SettingsService.Settings.SkinModel;

    public string AccountStatus =>
        LanguageService.Translate(
            string.Equals(SettingsService.Settings.AccountType, "微软账户", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(SettingsService.Settings.MicrosoftAccessToken)
                ? "微软账户 · 正版登录"
                : "离线账户 · 本地皮肤");

    public string MemoryText => $"{SettingsService.Settings.MemoryMb:N0} MB";

    public string VersionText => "v1.4.0";

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
    }

    public string BusyMessage
    {
        get => _busyMessage;
        private set => SetProperty(ref _busyMessage, value);
    }

    public bool IsGameRunning => LaunchLog.IsRunning;

    public bool ShowGameChip => LaunchLog.IsRunning;

    public string GameChipText => $"游戏运行中 · {LaunchLog.InstanceId}";

    public string ToastMessage
    {
        get => _toastMessage;
        private set => SetProperty(ref _toastMessage, value);
    }

    public bool ToastVisible
    {
        get => _toastVisible;
        private set => SetProperty(ref _toastVisible, value);
    }

    public bool ToastIsError
    {
        get => _toastIsError;
        private set => SetProperty(ref _toastIsError, value);
    }

    public bool ShowEmptyState => !IsBusy && Instances.Count == 0;

    public RelayCommand GoHomeCommand { get; }
    public RelayCommand GoSettingsCommand { get; }
    public RelayCommand GoAccountCommand { get; }
    public RelayCommand GoDownloadCommand { get; }
    public RelayCommand OpenSettingsCommand { get; }
    public RelayCommand OpenInstanceCommand { get; }
    public RelayCommand SelectInstanceCommand { get; }
    public RelayCommand LaunchCommand { get; }
    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenRootFolderCommand { get; }
    public RelayCommand OpenGameLogCommand { get; }
    public RelayCommand OpenInstanceModsCommand { get; }
    public RelayCommand OpenInstanceTestCommand { get; }

    public async Task InitializeAsync()
    {
        BusyMessage = "正在扫描游戏版本…";
        IsBusy = true;
        try
        {
            var javas = await JavaService.DiscoverAsync();
            JavaRuntimes.Clear();
            foreach (var java in javas)
            {
                JavaRuntimes.Add(java);
            }

            await LoadHomeJavaOptionsAsync();

            RootPath = InstallationService.ResolveRoot() ?? "";
            if (string.IsNullOrWhiteSpace(RootPath))
            {
                Toast("没有找到 Minecraft 目录，请先在设置中指定 .minecraft 路径。", true);
                Instances.Clear();
                return;
            }

            SettingsService.Settings.MinecraftRoot = RootPath;
            SettingsService.Save();
            await ReloadInstancesCoreAsync();
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    public async Task ReloadInstancesAsync(bool showToast = false)
    {
        BusyMessage = "正在刷新版本列表…";
        IsBusy = true;
        try
        {
            RootPath = InstallationService.ResolveRoot() ?? RootPath;
            if (!string.IsNullOrWhiteSpace(RootPath))
            {
                await ReloadInstancesCoreAsync();
                if (showToast)
                {
                    Toast($"已刷新，共 {Instances.Count} 个版本。", false);
                }
            }
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ShowEmptyState));
        }
    }

    private async Task ReloadInstancesCoreAsync()
    {
        var selectedId = SelectedInstance?.Id ?? SettingsService.Settings.LastInstance;
        var instances = await InstallationService.LoadInstancesAsync(RootPath);
        _suppressSelectionSave = true;
        Instances.Clear();
        foreach (var instance in instances)
        {
            Instances.Add(instance);
        }

        var selected = Instances.FirstOrDefault(i => i.Id == selectedId) ?? Instances.FirstOrDefault();
        SelectedInstance = selected;
        ApplyInstanceFilter();
        _suppressSelectionSave = false;

        OnPropertyChanged(nameof(InstanceCount));
        OnPropertyChanged(nameof(FilteredInstanceCount));
        OnPropertyChanged(nameof(InstanceCountText));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(IsHomeFilterEmpty));
        if (instances.Count == 0)
        {
            Toast("该目录下没有可用的游戏版本。", true);
        }
    }

    private void ApplyInstanceFilter()
    {
        var loaderKey = SelectedHomeLoader?.Key ?? "all";
        var typeKey = SelectedHomeType?.Key ?? "all";
        var search = InstanceSearch.Trim();
        var previous = SelectedInstance;

        FilteredInstances.Clear();
        foreach (var instance in Instances)
        {
            if (!MatchesLoaderFilter(instance, loaderKey) || !MatchesTypeFilter(instance, typeKey))
            {
                continue;
            }

            if (search.Length > 0 &&
                !instance.Id.Contains(search, StringComparison.OrdinalIgnoreCase) &&
                !instance.Subtitle.Contains(search, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            FilteredInstances.Add(instance);
        }

        OnPropertyChanged(nameof(FilteredInstanceCount));
        OnPropertyChanged(nameof(InstanceCountText));
        OnPropertyChanged(nameof(IsHomeFilterEmpty));

        var next = previous != null && FilteredInstances.Contains(previous)
            ? previous
            : FilteredInstances.FirstOrDefault();
        if (next != null && !ReferenceEquals(SelectedInstance, next))
        {
            _suppressSelectionSave = true;
            SelectedInstance = next;
            _suppressSelectionSave = false;
        }
    }

    private static bool MatchesLoaderFilter(GameInstance instance, string key)
    {
        var loader = instance.Loader;
        var isNeoForge = loader.Contains("NeoForge", StringComparison.OrdinalIgnoreCase);
        var isForge = loader.Contains("Forge", StringComparison.OrdinalIgnoreCase) && !isNeoForge;
        return key switch
        {
            "vanilla" => loader.Equals("原版", StringComparison.OrdinalIgnoreCase) ||
                         loader.Equals("Vanilla", StringComparison.OrdinalIgnoreCase),
            "neoforge" => isNeoForge,
            "forge" => isForge,
            "fabric" => loader.Contains("Fabric", StringComparison.OrdinalIgnoreCase),
            "quilt" => loader.Contains("Quilt", StringComparison.OrdinalIgnoreCase),
            "optifine" => loader.Contains("OptiFine", StringComparison.OrdinalIgnoreCase),
            _ => true
        };
    }

    private static bool MatchesTypeFilter(GameInstance instance, string key)
        => key switch
        {
            "release" => string.Equals(instance.Type, "release", StringComparison.OrdinalIgnoreCase),
            "snapshot" => string.Equals(instance.Type, "snapshot", StringComparison.OrdinalIgnoreCase),
            "old" => instance.Type.StartsWith("old_", StringComparison.OrdinalIgnoreCase),
            _ => true
        };

    private static bool SelectSingleFilter(IEnumerable<DownloadFilterOption> options, DownloadFilterOption selected)
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

    public void OpenInstance(GameInstance instance, InstanceSection section = InstanceSection.Overview)
    {
        CurrentPage = new InstanceViewModel(this, instance, section);
        IsInstancePage = true;
        IsSettingsPage = false;
        IsAccountPage = false;
        IsDownloadPage = false;
        IsHome = false;
        HeaderTitle = LanguageService.Translate("版本设置");
        HeaderSubtitle = $"{instance.Id} · {instance.Loader}";
        OnPropertyChanged(nameof(ShowBack));
    }

    private void GoHome()
    {
        CurrentPage = this;
        IsHome = true;
        IsInstancePage = false;
        IsSettingsPage = false;
        IsAccountPage = false;
        IsDownloadPage = false;
        HeaderTitle = "";
        HeaderSubtitle = "";
        OnPropertyChanged(nameof(ShowBack));
    }

    private void GoSettings()
    {
        _settingsPage ??= new SettingsViewModel(SettingsService, JavaService, Toast, currentInstance: () => SelectedInstance, saved: async () =>
        {
            await ReloadInstancesAsync();
            OnPropertyChanged(nameof(PlayerName));
            OnPropertyChanged(nameof(PlayerInitial));
            OnPropertyChanged(nameof(MemoryText));
        });
        _settingsPage.RefreshSkin();
        CurrentPage = _settingsPage;
        IsSettingsPage = true;
        IsInstancePage = false;
        IsAccountPage = false;
        IsDownloadPage = false;
        IsHome = false;
        HeaderTitle = LanguageService.Translate("启动器设置");
        HeaderSubtitle = LanguageService.Translate("Java、内存、账户与游戏目录");
        OnPropertyChanged(nameof(ShowBack));
    }

    private void GoAccount()
    {
        var app = Application.Current as App;
        if (app == null)
        {
            return;
        }

        var account = new AccountViewModel(SettingsService, app.Skins, app.Mojang, Toast, OnAccountSaved);
        CurrentPage = account;
        IsSettingsPage = false;
        IsInstancePage = false;
        IsAccountPage = true;
        IsDownloadPage = false;
        IsHome = false;
        HeaderTitle = LanguageService.Translate("账户与皮肤");
        HeaderSubtitle = LanguageService.Translate("离线账户、3D 皮肤预览与正版皮肤查询");
        OnPropertyChanged(nameof(ShowBack));
    }

    private void GoDownload()
    {
        _downloadPage ??= new DownloadViewModel(
            SettingsService,
            MojangGameService,
            FabricService,
            QuiltService,
            ModrinthService,
            ModLoaderInstallerService,
            ModpackService,
            Toast,
            () => SelectedInstance,
            () => Instances,
            () => RootPath,
            () => ReloadInstancesAsync());
        _downloadPage.RefreshContext();
        CurrentPage = _downloadPage;
        IsSettingsPage = false;
        IsInstancePage = false;
        IsAccountPage = false;
        IsDownloadPage = true;
        IsHome = false;
        HeaderTitle = LanguageService.Translate("下载");
        HeaderSubtitle = LanguageService.Translate("游戏版本、模组、资源包与光影");
        OnPropertyChanged(nameof(ShowBack));
    }

    private void OnAccountSaved()
    {
        OnPropertyChanged(nameof(PlayerName));
        OnPropertyChanged(nameof(PlayerInitial));
        OnPropertyChanged(nameof(SkinPath));
        OnPropertyChanged(nameof(SkinModel));
        OnPropertyChanged(nameof(AccountStatus));
    }

    private void LaunchSelected()
    {
        if (SelectedInstance == null)
        {
            Toast("请先选择一个游戏版本。", true);
            return;
        }

        if (LaunchLog.IsRunning)
        {
            Toast($"「{LaunchLog.InstanceId}」正在运行，已为你打开日志。", false);
            OpenGameLog();
            return;
        }

        LaunchLog.Start(SelectedInstance, testMode: false);
        Toast($"正在启动「{SelectedInstance.Id}」，可在右上角查看运行日志。", false);
    }

    private void OpenGameLog()
    {
        var instance = LaunchLog.Instance ?? SelectedInstance;
        if (instance == null)
        {
            return;
        }

        var page = new InstanceViewModel(this, instance);
        CurrentPage = page;
        page.ShowExistingLog();
        IsInstancePage = true;
        IsSettingsPage = false;
        IsAccountPage = false;
        IsDownloadPage = false;
        IsHome = false;
        HeaderTitle = LanguageService.Translate("版本设置");
        HeaderSubtitle = $"{instance.Id} · {instance.Loader}";
        OnPropertyChanged(nameof(ShowBack));
    }

    private void OpenRootFolder()
    {
        try
        {
            if (Directory.Exists(RootPath))
            {
                Process.Start(new ProcessStartInfo(RootPath) { UseShellExecute = true });
            }
            else
            {
                Toast("Minecraft 目录不存在。", true);
            }
        }
        catch (Exception ex)
        {
            Toast($"无法打开目录：{ex.Message}", true);
        }
    }

    public void Toast(string message, bool isError = false)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        if (!dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(new Action(() => Toast(message, isError)));
            return;
        }

        ToastMessage = LanguageService.Translate(message);
        ToastIsError = isError;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    /// <summary>填充运行环境面板的 Java 下拉：自动选择 + 已检测到的运行时。</summary>
    public async Task LoadHomeJavaOptionsAsync()
    {
        _homeJavaLoading = true;
        try
        {
            var installed = JavaRuntimes.Count > 0 ? JavaRuntimes.ToList() : (await JavaService.DiscoverAsync()).ToList();
            var current = SettingsService.Settings.JavaPath;

            HomeJavaOptions.Clear();
            var auto = new JavaOption
            {
                Path = "",
                Label = "自动选择（推荐）",
                Detail = "按版本要求挑选最合适的 Java",
                IsAuto = true
            };
            HomeJavaOptions.Add(auto);

            foreach (var java in installed)
            {
                HomeJavaOptions.Add(new JavaOption
                {
                    Path = java.Path,
                    Label = $"Java {java.Major} · {java.Version}",
                    Detail = java.Path,
                    Major = java.Major
                });
            }

            var selected = string.IsNullOrWhiteSpace(current)
                ? auto
                : HomeJavaOptions.FirstOrDefault(o =>
                    !o.IsAuto && string.Equals(o.Path, current, StringComparison.OrdinalIgnoreCase)) ?? auto;
            selected.IsSelected = true;
            _selectedHomeJava = selected;
            OnPropertyChanged(nameof(SelectedHomeJava));
        }
        finally
        {
            _homeJavaLoading = false;
        }
    }

    private async Task UpdateSelectedJavaAsync()
    {
        if (SelectedInstance == null)
        {
            SelectedJavaText = "未选择版本";
            return;
        }

        var instance = SelectedInstance;
        try
        {
            var java = await JavaService.ResolveAsync(instance.RequiredJavaMajor,
                SettingsService.Settings.JavaPath);
            if (ReferenceEquals(SelectedInstance, instance))
            {
                SelectedJavaText = java == null
                    ? $"缺少 Java {instance.RequiredJavaMajor}+"
                    : $"Java {java.Major} · {java.Version}";
            }
        }
        catch
        {
            SelectedJavaText = "检测失败";
        }
    }

    private void OnLaunchLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LaunchLogViewModel.IsRunning) or nameof(LaunchLogViewModel.InstanceId) or nameof(LaunchLogViewModel.Instance))
        {
            OnPropertyChanged(nameof(IsGameRunning));
            OnPropertyChanged(nameof(ShowGameChip));
            OnPropertyChanged(nameof(GameChipText));
            if (LaunchLog.IsRunning && SettingsService.Settings.CloseLauncherOnGameStart &&
                Application.Current?.MainWindow != null)
            {
                Application.Current.MainWindow.WindowState = WindowState.Minimized;
            }
        }
    }
}




