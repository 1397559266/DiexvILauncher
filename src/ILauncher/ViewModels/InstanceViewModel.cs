using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.Localization;
using ILauncher.Models;

namespace ILauncher.ViewModels;

public sealed class InstanceViewModel : ObservableObject
{
    private readonly MainViewModel _main;
    private readonly SettingsService _settings;
    private readonly JavaService _javaService;
    private readonly ModService _modService;
    private readonly Dictionary<InstanceSection, object> _pages = new();
    private NavItem? _selectedNav;
    private object? _currentModule;
    private bool _isTestActive;

    public InstanceViewModel(MainViewModel main, GameInstance instance, InstanceSection initialSection = InstanceSection.Overview)
    {
        _main = main;
        _settings = main.SettingsService;
        _javaService = main.JavaService;
        _modService = main.ModService;
        Instance = instance;

        NavItems = new ObservableCollection<NavItem>
        {
            NavItem(InstanceSection.Overview, "概览", "IconDashboard"),
            NavItem(InstanceSection.Settings, "设置", "IconSettings"),
            NavItem(InstanceSection.Mods, "模组管理", "IconPuzzle"),
            NavItem(InstanceSection.ResourcePacks, "资源包", "IconLayers"),
            NavItem(InstanceSection.Shaders, "光影", "IconSparkles"),
            NavItem(InstanceSection.Saves, "存档", "IconGlobe"),
            NavItem(InstanceSection.Screenshots, "截图", "IconImage"),
            NavItem(InstanceSection.Servers, "服务器", "IconServer")
        };

        TestGameCommand = new RelayCommand(ShowTestGame);
        LaunchGameCommand = new RelayCommand(() => BeginLaunch(testMode: false));
        OpenModsCommand = new RelayCommand(() =>
        {
            SelectedNav = NavItems.First(n => n.Section == InstanceSection.Mods);
        });
        OpenSettingsCommand = new RelayCommand(() =>
        {
            SelectedNav = NavItems.First(n => n.Section == InstanceSection.Settings);
        });

        Mods = new ModsViewModel(_modService, _main.ModrinthService, _main.Toast, OnModsChanged);
        Mods.Attach(Instance);
        _pages[InstanceSection.Mods] = Mods;

        Overview = new OverviewViewModel(
            instance,
            _javaService,
            _settings,
            _main.LaunchService,
            _main.Toast,
            LaunchGameCommand,
            new RelayCommand(ShowTestGame),
            OpenSettingsCommand);
        _pages[InstanceSection.Overview] = Overview;

        InstanceSettings = new InstanceSettingsViewModel(instance, _settings, _javaService, _main.Toast, () =>
        {
            _ = Overview.LoadAsync();
        });
        _pages[InstanceSection.Settings] = InstanceSettings;

        _main.LaunchLog.PropertyChanged += OnLogPropertyChanged;

        var start = initialSection == InstanceSection.Mods ? NavItems.First(n => n.Section == InstanceSection.Mods)
            : initialSection == InstanceSection.Settings ? NavItems.First(n => n.Section == InstanceSection.Settings)
            : NavItems[0];
        SelectedNav = start;
        _ = LoadAsync();
    }

    public GameInstance Instance { get; }

    public string Name => Instance.Id;
    public string Subtitle => Instance.Subtitle;
    public string TypeLabel => Instance.TypeLabel;
    public string ModsSummary => $"{Instance.ModCount} 模组";

    public ObservableCollection<NavItem> NavItems { get; }

    public ModsViewModel Mods { get; }
    public OverviewViewModel Overview { get; }
    public InstanceSettingsViewModel InstanceSettings { get; }

    public ICommand TestGameCommand { get; }
    public ICommand LaunchGameCommand { get; }
    public ICommand OpenModsCommand { get; }
    public ICommand OpenSettingsCommand { get; }

    public NavItem? SelectedNav
    {
        get => _selectedNav;
        set
        {
            if (!SetProperty(ref _selectedNav, value))
            {
                return;
            }

            if (value != null)
            {
                ActivateSection(value.Section);
            }
        }
    }

    public object? CurrentModule
    {
        get => _currentModule;
        private set
        {
            if (SetProperty(ref _currentModule, value))
            {
                // The module view is created by a DataTemplate after this
                // property changes, so give it a translation pass once it is
                // in the visual tree.
                Application.Current?.Dispatcher.BeginInvoke(new Action(LanguageService.Apply),
                    DispatcherPriority.Loaded);
                Application.Current?.Dispatcher.BeginInvoke(new Action(LanguageService.Apply),
                    DispatcherPriority.ApplicationIdle);
            }
        }
    }

    public bool IsTestActive
    {
        get => _isTestActive;
        private set => SetProperty(ref _isTestActive, value);
    }

    public bool IsTestRunning => _main.LaunchLog.IsRunning && _main.LaunchLog.Instance?.Id == Instance.Id;

    public string TestButtonHint => IsTestRunning
        ? "测试运行中 · 点击查看日志"
        : "启动并查看全部游戏日志";

    private NavItem NavItem(InstanceSection section, string title, string iconKey) => new()
    {
        Title = title,
        Section = section,
        IconData = UiResources.Geometry(iconKey)
    };

    private async Task LoadAsync()
    {
        await Task.WhenAll(Mods.LoadAsync(), Overview.LoadAsync());
        Instance.ModCount = Mods.Mods.Count;
        Instance.EnabledModCount = Mods.EnabledCount;
        Overview.RefreshModSummary();
        OnPropertyChanged(nameof(ModsSummary));
        OnPropertyChanged(nameof(TestButtonHint));
    }

    private void ActivateSection(InstanceSection section)
    {
        IsTestActive = false;
        if (!_pages.TryGetValue(section, out var page))
        {
            switch (section)
            {
                case InstanceSection.Overview:
                    page = Overview;
                    break;
                case InstanceSection.Mods:
                    page = Mods;
                    Mods.Attach(Instance);
                    break;
                case InstanceSection.Settings:
                    page = InstanceSettings;
                    break;
                case InstanceSection.Servers:
                    var servers = new ServersViewModel(Instance, JoinServer, _main.Toast);
                    _pages[section] = servers;
                    page = servers;
                    break;
                default:
                    var browser = new FolderBrowserViewModel(Instance, section, _main.Toast);
                    _pages[section] = browser;
                    _ = browser.LoadAsync();
                    page = browser;
                    break;
            }

            if (page != null)
            {
                _pages[section] = page;
            }

            if (page is FolderBrowserViewModel browserPage)
            {
                _ = browserPage.LoadAsync();
            }
        }
        else if (page is FolderBrowserViewModel cachedBrowser)
        {
            _ = cachedBrowser.LoadAsync();
        }

        CurrentModule = page;
    }

    public void ShowExistingLog()
    {
        ShowLogPage();
    }

    private void JoinServer(string address)
    {
        if (_main.LaunchLog.IsRunning)
        {
            _main.Toast("已有游戏进程正在运行，请先停止当前游戏。", true);
            return;
        }

        ShowLogPage();
        _main.LaunchLog.Start(Instance, testMode: false, serverAddress: address);
        IsTestActive = true;
    }

    private void ShowTestGame()
    {
        if (_main.LaunchLog.IsRunning)
        {
            if (_main.LaunchLog.Instance?.Id == Instance.Id)
            {
                ShowLogPage();
                return;
            }

            _main.Toast($"正在测试「{_main.LaunchLog.InstanceId}」，请先停止当前游戏。", true);
            return;
        }

        ShowLogPage();
        _main.LaunchLog.Start(Instance, testMode: true);
        IsTestActive = true;
    }

    private void BeginLaunch(bool testMode)
    {
        if (_main.LaunchLog.IsRunning)
        {
            _main.Toast("已有游戏进程正在运行。", true);
            return;
        }

        ShowLogPage();
        _main.LaunchLog.Start(Instance, testMode);
        IsTestActive = true;
    }

    private void ShowLogPage()
    {
        SelectedNav = null;
        CurrentModule = _main.LaunchLog;
        IsTestActive = true;
        OnPropertyChanged(nameof(IsTestRunning));
        OnPropertyChanged(nameof(TestButtonHint));
    }

    private void OnModsChanged()
    {
        Instance.ModCount = Mods.Mods.Count;
        Instance.EnabledModCount = Mods.EnabledCount;
        OnPropertyChanged(nameof(ModsSummary));
    }

    private void OnLogPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(LaunchLogViewModel.IsRunning) or nameof(LaunchLogViewModel.Instance)
            or nameof(LaunchLogViewModel.InstanceId) or nameof(LaunchLogViewModel.IsFinished)
            or nameof(LaunchLogViewModel.StatusText))
        {
            OnPropertyChanged(nameof(IsTestRunning));
            OnPropertyChanged(nameof(TestButtonHint));
            if (IsTestActive && !_main.LaunchLog.IsRunning && CurrentModule is LaunchLogViewModel)
            {
                // Keep the finished log visible; the test button simply returns to it.
            }

            CommandManager.InvalidateRequerySuggested();
        }
    }
}
