using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ILauncher.Core;
using ILauncher.Core.Services;
using ILauncher.Localization;
using ILauncher.Models;

namespace ILauncher.Views;

public partial class ModVersionWindow : Window, INotifyPropertyChanged
{
    private readonly Func<string, string?, Task<IReadOnlyList<ModrinthVersion>>> _loadVersions;
    private readonly Func<ModrinthVersion, string, string?, Task<IReadOnlyList<ModDependencyItem>>> _analyzeDependencies;
    private readonly Func<ModrinthVersion, string, string?, Task<DependencyLoadResult>> _loadJarDependencies;
    private readonly Func<ModrinthVersion, string, string?, Action<DownloadProgress>?, Task> _installMissingDependencies;
    private readonly Func<ModDependencyItem, string, string?, Action<DownloadProgress>?, Task> _installDependency;
    private readonly List<ModrinthVersion> _allVersions = new();
    private string _selectedGameVersion;
    private string? _selectedLoader;
    private string _selectedReleaseFilter = "全部";
    private ModrinthVersion? _selectedVersion;
    private bool _isLoading;
    private bool _isAnalyzing;
    private bool _isJarLoading;
    private bool _hasJarError;
    private bool _isInstallingDependencies;
    private string _statusText = "";
    private string _dependencyStatus = "";
    private int _missingRequiredCount;
    private int _unresolvedRequiredCount;
    private bool _loaded;

    public ModVersionWindow(ModrinthProject project, string initialGameVersion,
        IReadOnlyList<string> gameVersions, string? initialLoader, IReadOnlyList<string> loaders,
        Func<string, string?, Task<IReadOnlyList<ModrinthVersion>>> loadVersions,
        Func<ModrinthVersion, string, string?, Task<IReadOnlyList<ModDependencyItem>>> analyzeDependencies,
        Func<ModrinthVersion, string, string?, Task<DependencyLoadResult>> loadJarDependencies,
        Func<ModrinthVersion, string, string?, Action<DownloadProgress>?, Task> installMissingDependencies,
        Func<ModDependencyItem, string, string?, Action<DownloadProgress>?, Task> installDependency)
    {
        Project = project;
        GameVersions = gameVersions;
        Loaders = loaders;
        ShowLoader = loaders.Count > 0;
        _selectedGameVersion = initialGameVersion;
        _selectedLoader = initialLoader;
        _loadVersions = loadVersions;
        _analyzeDependencies = analyzeDependencies;
        _loadJarDependencies = loadJarDependencies;
        _installMissingDependencies = installMissingDependencies;
        _installDependency = installDependency;
        InitializeComponent();
        _selectedReleaseFilter = ReleaseFilters[0];
        InstallDependencyCommand = new AsyncRelayCommand(async parameter =>
        {
            if (parameter is ModDependencyItem item && item.CanInstall && SelectedVersion != null)
            {
                await InstallSingleDependencyAsync(item);
            }
        });
        OpenLinkCommand = new RelayCommand(parameter =>
        {
            if (parameter is string url && !string.IsNullOrWhiteSpace(url))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                }
                catch
                {
                    // Ignore a browser launch failure.
                }
            }
        });
        DataContext = this;
        Loaded += OnLoaded;
    }

    public ModrinthProject Project { get; }
    public IReadOnlyList<string> GameVersions { get; }
    public IReadOnlyList<string> Loaders { get; }
    public bool ShowLoader { get; }
    public IReadOnlyList<string> ReleaseFilters => LanguageService.CurrentLanguage == LanguageService.English
        ? new[] { "All", "Release", "Beta", "Alpha" }
        : new[] { "全部", "正式版", "Beta", "Alpha" };
    public ObservableCollection<ModrinthVersion> Versions { get; } = new();
    public ObservableCollection<ModDependencyItem> Dependencies { get; } = new();
    public AsyncRelayCommand InstallDependencyCommand { get; }
    public RelayCommand OpenLinkCommand { get; }

    public string SelectedGameVersion
    {
        get => _selectedGameVersion;
        set
        {
            if (!Set(ref _selectedGameVersion, value) || !_loaded)
            {
                return;
            }

            _ = LoadVersionsAsync(value);
        }
    }

    public string? SelectedLoader
    {
        get => _selectedLoader;
        set
        {
            if (!Set(ref _selectedLoader, value) || !_loaded)
            {
                return;
            }

            _ = LoadVersionsAsync(SelectedGameVersion);
        }
    }

    public string SelectedReleaseFilter
    {
        get => _selectedReleaseFilter;
        set
        {
            if (Set(ref _selectedReleaseFilter, value) && _loaded)
            {
                ApplyReleaseFilter();
            }
        }
    }

    public ModrinthVersion? SelectedVersion
    {
        get => _selectedVersion;
        set
        {
            if (Set(ref _selectedVersion, value) && _loaded)
            {
                _ = AnalyzeDependenciesAsync();
            }
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        private set
        {
            if (Set(ref _isAnalyzing, value))
            {
                OnPropertyChanged(nameof(ShowDependencyPanel));
                OnPropertyChanged(nameof(ShowDependencySearching));
                OnPropertyChanged(nameof(ShowNoDependencies));
            }
        }
    }

    public bool IsJarLoading
    {
        get => _isJarLoading;
        private set
        {
            if (Set(ref _isJarLoading, value))
            {
                OnPropertyChanged(nameof(ShowDependencyPanel));
                OnPropertyChanged(nameof(ShowDependencySearching));
                OnPropertyChanged(nameof(ShowNoDependencies));
            }
        }
    }

    public bool ShowDependencyPanel => HasDependencies || IsJarLoading || HasJarError || IsAnalyzing;
    public bool ShowDependencySearching => IsAnalyzing || IsJarLoading;
    public bool ShowNoDependencies => !HasDependencies && !ShowDependencySearching && !HasJarError;

    public bool HasJarError
    {
        get => _hasJarError;
        private set
        {
            if (Set(ref _hasJarError, value))
            {
                OnPropertyChanged(nameof(ShowDependencyPanel));
                OnPropertyChanged(nameof(ShowNoDependencies));
            }
        }
    }

    public bool IsInstallingDependencies
    {
        get => _isInstallingDependencies;
        private set => Set(ref _isInstallingDependencies, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, LanguageService.Translate(value));
    }

    public string DependencyStatus
    {
        get => _dependencyStatus;
        private set => Set(ref _dependencyStatus, LanguageService.Translate(value));
    }

    public int MissingRequiredCount
    {
        get => _missingRequiredCount;
        private set
        {
            if (Set(ref _missingRequiredCount, value))
            {
                OnPropertyChanged(nameof(HasMissingRequired));
            }
        }
    }

    public bool HasDependencies => Dependencies.Count > 0;
    public bool HasMissingRequired => MissingRequiredCount > 0;
    public int UnresolvedRequiredCount
    {
        get => _unresolvedRequiredCount;
        private set
        {
            if (Set(ref _unresolvedRequiredCount, value))
            {
                OnPropertyChanged(nameof(HasUnresolvedRequired));
            }
        }
    }

    public bool HasUnresolvedRequired => UnresolvedRequiredCount > 0;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        _loaded = true;
        await LoadVersionsAsync(SelectedGameVersion);
    }

    private async Task LoadVersionsAsync(string gameVersion)
    {
        IsLoading = true;
        StatusText = "正在获取兼容文件…";
        Dependencies.Clear();
        OnPropertyChanged(nameof(HasDependencies));
            OnPropertyChanged(nameof(ShowNoDependencies));
            OnPropertyChanged(nameof(ShowDependencyPanel));
        MissingRequiredCount = 0;
        UnresolvedRequiredCount = 0;
        try
        {
            var versions = await _loadVersions(gameVersion, SelectedLoader);
            _allVersions.Clear();
            _allVersions.AddRange(versions);
            ApplyReleaseFilter();
            StatusText = Versions.Count == 0
                ? "没有适用于该版本和加载器的文件"
                : $"共 {Versions.Count} 个文件版本";
        }
        catch (Exception ex)
        {
            Versions.Clear();
            _allVersions.Clear();
            SelectedVersion = null;
            StatusText = $"获取失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyReleaseFilter()
    {
        var selectedId = SelectedVersion?.Id;
        Versions.Clear();
        foreach (var version in _allVersions)
        {
            var include = SelectedReleaseFilter switch
            {
                "正式版" or "Release" => version.ReleaseType == "release",
                "Beta" => version.ReleaseType == "beta",
                "Alpha" => version.ReleaseType == "alpha",
                _ => true
            };
            if (include)
            {
                Versions.Add(version);
            }
        }

        SelectedVersion = Versions.FirstOrDefault(version => version.Id == selectedId) ?? Versions.FirstOrDefault();
        StatusText = Versions.Count == 0
            ? "该通道没有可用文件"
            : $"共 {Versions.Count} 个文件版本";
    }

    private async Task AnalyzeDependenciesAsync()
    {
        if (!ShowLoader || SelectedVersion == null)
        {
            Dependencies.Clear();
            OnPropertyChanged(nameof(HasDependencies));
            OnPropertyChanged(nameof(ShowNoDependencies));
            OnPropertyChanged(nameof(ShowDependencyPanel));
            MissingRequiredCount = 0;
            UnresolvedRequiredCount = 0;
            DependencyStatus = "";
            return;
        }

        IsAnalyzing = true;
        HasJarError = false;
        DependencyStatus = "正在检测前置…";
        var selectedVersion = SelectedVersion;
        try
        {
            var items = await _analyzeDependencies(selectedVersion, SelectedGameVersion, SelectedLoader);
            Dependencies.Clear();
            foreach (var item in items)
            {
                Dependencies.Add(item);
            }

            if (Dependencies.Count == 0 && selectedVersion.File != null)
            {
                DependencyStatus = "正在搜索前置…";
                OnPropertyChanged(nameof(ShowDependencySearching));
                OnPropertyChanged(nameof(ShowNoDependencies));
                OnPropertyChanged(nameof(ShowDependencyPanel));
            }
            else
            {
                UpdateDependencySummary();
            }

            _ = LoadJarDependenciesAsync(selectedVersion);
        }
        catch (Exception ex)
        {
            Dependencies.Clear();
            MissingRequiredCount = 0;
            UnresolvedRequiredCount = 0;
            OnPropertyChanged(nameof(HasDependencies));
            OnPropertyChanged(nameof(ShowNoDependencies));
            OnPropertyChanged(nameof(ShowDependencyPanel));
            DependencyStatus = $"前置检测失败：{ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
    }

    private async Task LoadJarDependenciesAsync(ModrinthVersion version)
    {
        if (!ShowLoader || version.File == null)
        {
            return;
        }

        IsJarLoading = true;
        DependencyStatus = Dependencies.Count == 0 ? "正在读取 JAR 前置…" : "正在补充 JAR 前置…";
        try
        {
            var loadResult = await _loadJarDependencies(version, SelectedGameVersion, SelectedLoader);
            if (SelectedVersion?.Id != version.Id)
            {
                return;
            }

            if (!loadResult.Success)
            {
                HasJarError = true;
                DependencyStatus = loadResult.Error is { Length: > 0 }
                    ? $"读取 JAR 前置失败：{loadResult.Error}"
                    : "读取 JAR 前置失败";
                OnPropertyChanged(nameof(ShowDependencyPanel));
                return;
            }

            HasJarError = false;
            foreach (var item in loadResult.Items)
            {
                if (Dependencies.Any(existing =>
                        existing.ProjectId == item.ProjectId ||
                        string.Equals(existing.Title, item.Title, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                Dependencies.Add(item);
            }

            UpdateDependencySummary();
        }
        catch (Exception ex)
        {
            HasJarError = true;
            DependencyStatus = $"JAR 前置读取失败：{ex.Message}";
            OnPropertyChanged(nameof(ShowDependencyPanel));
        }
        finally
        {
            IsJarLoading = false;
            if (SelectedVersion?.Id == version.Id)
            {
                OnPropertyChanged(nameof(ShowDependencyPanel));
            }
        }
    }

    private void UpdateDependencySummary()
    {
        MissingRequiredCount = Dependencies.Count(item => item.Missing);
        UnresolvedRequiredCount = Dependencies.Count(item => item.UnresolvedRequired);
        OnPropertyChanged(nameof(HasDependencies));
            OnPropertyChanged(nameof(ShowNoDependencies));
        OnPropertyChanged(nameof(ShowDependencyPanel));
        DependencyStatus = Dependencies.Count == 0
            ? "没有前置模组"
            : $"共 {Dependencies.Count} 个前置 · 缺少 {MissingRequiredCount} 个必需" +
              (UnresolvedRequiredCount > 0 ? $" · {UnresolvedRequiredCount} 个无法解析" : "");
    }

    private async Task InstallSingleDependencyAsync(ModDependencyItem item)
    {
        if (IsInstallingDependencies || IsJarLoading)
        {
            return;
        }

        IsInstallingDependencies = true;
        DependencyStatus = $"正在安装前置 {item.Title}…";
        try
        {
            await _installDependency(item, SelectedGameVersion, SelectedLoader,
                progress => DependencyStatus = progress.Message);
            await AnalyzeDependenciesAsync();
        }
        catch (Exception ex)
        {
            DependencyStatus = $"安装前置失败：{ex.Message}";
        }
        finally
        {
            IsInstallingDependencies = false;
        }
    }

    private async void RetryDependencies_Click(object sender, RoutedEventArgs e)
    {
        if (IsAnalyzing || IsJarLoading)
        {
            return;
        }

        await AnalyzeDependenciesAsync();
    }

    private async void InstallDependencies_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedVersion == null || IsInstallingDependencies)
        {
            return;
        }

        IsInstallingDependencies = true;
        DependencyStatus = "正在安装缺失前置…";
        try
        {
            await _installMissingDependencies(SelectedVersion, SelectedGameVersion, SelectedLoader,
                progress => DependencyStatus = progress.Message);
            await AnalyzeDependenciesAsync();
        }
        catch (Exception ex)
        {
            DependencyStatus = $"安装前置失败：{ex.Message}";
        }
        finally
        {
            IsInstallingDependencies = false;
        }
    }

    private void VersionsList_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not ListBox list)
        {
            return;
        }

        var item = FindAncestor<ListBoxItem>(e.OriginalSource as DependencyObject);
        if (item?.DataContext is ModrinthVersion version)
        {
            list.SelectedItem = version;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
    {
        while (current != null)
        {
            if (current is T match)
            {
                return match;
            }

            current = current is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(current)
                : LogicalTreeHelper.GetParent(current);
        }

        return null;
    }

    private void Install_Click(object sender, RoutedEventArgs e)
    {
        if (IsLoading)
        {
            MessageBox.Show("文件版本还在加载中，请稍候。", "安装模组", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (IsJarLoading)
        {
            MessageBox.Show("前置模组还在检测中，请稍候。", "安装模组", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (HasJarError)
        {
            MessageBox.Show("前置模组读取失败，请点击「重新检测前置」后重试。",
                "安装模组", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (SelectedVersion == null)
        {
            MessageBox.Show("请选择一个文件版本。", "安装模组", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (MissingRequiredCount > 0)
        {
            MessageBox.Show($"还缺少 {MissingRequiredCount} 个必需前置，请先点击「一键安装缺失前置」。",
                "缺少前置", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}



