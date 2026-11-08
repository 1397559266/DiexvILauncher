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
using ILauncher.Models;

namespace ILauncher.ViewModels;

public sealed class MainViewModel : ObservableObject
{
    private readonly DispatcherTimer _toastTimer;
    private object? _currentPage;
    private GameInstance? _selectedInstance;
    private string _instanceSearch = "";
    private string _rootPath = "";
    private string _selectedJavaText = "检测中…";
    private bool _isBusy;
    private bool _isHome = true;
    private bool _isSettingsPage;
    private bool _isAccountPage;
    private bool _isDownloadPage;
    private bool _isInstancePage;
    private DownloadViewModel? _downloadPage;
    private string _headerTitle = "";
    private string _headerSubtitle = "";
    private string _toastMessage = "";
    private bool _toastVisible;
    private bool _toastIsError;
    private bool _suppressSelectionSave;

    public MainViewModel()
    {
        SettingsService = new SettingsService();
        if (string.Equals(SettingsService.Settings.AccountType, "微软账户", StringComparison.Ordinal) &&
            !string.IsNullOrWhiteSpace(SettingsService.Settings.MicrosoftAccessTokenProtected))
        {
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(SettingsService.Settings.MicrosoftAccessTokenProtected), null,
                    DataProtectionScope.CurrentUser);
                SettingsService.Settings.MicrosoftAccessToken = Encoding.UTF8.GetString(bytes);
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
        SetHomeLoaderFilterCommand = new RelayCommand(parameter => SelectHomeLoader(parameter));
        SetHomeTypeFilterCommand = new RelayCommand(parameter => SelectHomeType(parameter));

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
    public RelayCommand SetHomeLoaderFilterCommand { get; }
    public RelayCommand SetHomeTypeFilterCommand { get; }
    public ObservableCollection<JavaInfo> JavaRuntimes { get; }

    public object? CurrentPage
    {
        get => _currentPage;
        private set
        {
            if (SetProperty(ref _currentPage, value))
            {
                OnPropertyChanged(nameof(IsHome));
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
    public string SelectedModsText => SelectedInstance == null ? "" : $"{SelectedInstance.ModCount} 个模组";
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

    public DownloadFilterOption? SelectedHomeLoader => HomeLoaderFilters.FirstOrDefault(option => option.IsSelected);
    public DownloadFilterOption? SelectedHomeType => HomeTypeFilters.FirstOrDefault(option => option.IsSelected);

    public bool IsHome
    {
        get => _isHome;
        private set => SetProperty(ref _isHome, value);
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
        private set => SetProperty(ref _isInstancePage, value);
    }

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

    public string PlayerName => SettingsService.ResolvePlayerName();

    public string PlayerInitial => string.IsNullOrWhiteSpace(PlayerName) ? "P" : PlayerName[..1].ToUpperInvariant();

    public string SkinPath
    {
        get
        {
            var skins = (Application.Current as App)?.Skins;
            return skins?.Resolve(SettingsService.Settings.SkinPath) ?? SettingsService.Settings.SkinPath;
        }
    }

    public string SkinModel => SettingsService.Settings.SkinModel;

    public string AccountStatus =>
        string.Equals(SettingsService.Settings.AccountType, "微软账户", StringComparison.Ordinal) &&
        !string.IsNullOrWhiteSpace(SettingsService.Settings.MicrosoftAccessToken)
            ? "微软账户 · 正版登录"
            : "离线账户 · 本地皮肤";

    public string MemoryText => $"{SettingsService.Settings.MemoryMb:N0} MB";

    public string VersionText => "v1.1.0";

    public bool IsBusy
    {
        get => _isBusy;
        private set => SetProperty(ref _isBusy, value);
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
        IsBusy = true;
        try
        {
            var javas = await JavaService.DiscoverAsync();
            JavaRuntimes.Clear();
            foreach (var java in javas)
            {
                JavaRuntimes.Add(java);
            }

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

    private void SelectHomeLoader(object? parameter)
    {
        if (parameter is DownloadFilterOption option && SelectSingleFilter(HomeLoaderFilters, option))
        {
            OnPropertyChanged(nameof(SelectedHomeLoader));
            ApplyInstanceFilter();
        }
    }

    private void SelectHomeType(object? parameter)
    {
        if (parameter is DownloadFilterOption option && SelectSingleFilter(HomeTypeFilters, option))
        {
            OnPropertyChanged(nameof(SelectedHomeType));
            ApplyInstanceFilter();
        }
    }

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
        HeaderTitle = "版本设置";
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
        HeaderTitle = "";
        HeaderSubtitle = "";
        OnPropertyChanged(nameof(ShowBack));
    }

    private void GoSettings()
    {
        var settings = new SettingsViewModel(SettingsService, JavaService, Toast, async () =>
        {
            await ReloadInstancesAsync();
            OnPropertyChanged(nameof(PlayerName));
            OnPropertyChanged(nameof(PlayerInitial));
            OnPropertyChanged(nameof(MemoryText));
        });
        CurrentPage = settings;
        IsSettingsPage = true;
        IsInstancePage = false;
        IsAccountPage = false;
        IsDownloadPage = false;
        IsHome = false;
        HeaderTitle = "启动器设置";
        HeaderSubtitle = "Java、内存、账户与游戏目录";
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
        HeaderTitle = "账户与皮肤";
        HeaderSubtitle = "离线账户、3D 皮肤预览与正版皮肤查询";
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
            () => RootPath,
            () => ReloadInstancesAsync());
        _downloadPage.RefreshContext();
        CurrentPage = _downloadPage;
        IsSettingsPage = false;
        IsInstancePage = false;
        IsAccountPage = false;
        IsDownloadPage = true;
        IsHome = false;
        HeaderTitle = "下载";
        HeaderSubtitle = "游戏版本、模组、资源包与光影";
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
        HeaderTitle = "版本设置";
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

        ToastMessage = message;
        ToastIsError = isError;
        ToastVisible = true;
        _toastTimer.Stop();
        _toastTimer.Start();
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


