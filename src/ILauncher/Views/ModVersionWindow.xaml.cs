using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using ILauncher.Core.Services;
using ILauncher.Models;

namespace ILauncher.Views;

public partial class ModVersionWindow : Window, INotifyPropertyChanged
{
    private readonly Func<string, string?, Task<IReadOnlyList<ModrinthVersion>>> _loadVersions;
    private readonly Func<ModrinthVersion, string, string?, Task<IReadOnlyList<ModDependencyItem>>> _analyzeDependencies;
    private readonly Func<ModrinthVersion, string, string?, Action<DownloadProgress>?, Task> _installMissingDependencies;
    private readonly List<ModrinthVersion> _allVersions = new();
    private string _selectedGameVersion;
    private string? _selectedLoader;
    private string _selectedReleaseFilter = "全部";
    private ModrinthVersion? _selectedVersion;
    private bool _isLoading;
    private bool _isAnalyzing;
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
        Func<ModrinthVersion, string, string?, Action<DownloadProgress>?, Task> installMissingDependencies)
    {
        Project = project;
        GameVersions = gameVersions;
        Loaders = loaders;
        ShowLoader = loaders.Count > 0;
        _selectedGameVersion = initialGameVersion;
        _selectedLoader = initialLoader;
        _loadVersions = loadVersions;
        _analyzeDependencies = analyzeDependencies;
        _installMissingDependencies = installMissingDependencies;
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
    }

    public ModrinthProject Project { get; }
    public IReadOnlyList<string> GameVersions { get; }
    public IReadOnlyList<string> Loaders { get; }
    public bool ShowLoader { get; }
    public IReadOnlyList<string> ReleaseFilters { get; } = new[] { "全部", "正式版", "Beta", "Alpha" };
    public ObservableCollection<ModrinthVersion> Versions { get; } = new();
    public ObservableCollection<ModDependencyItem> Dependencies { get; } = new();

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
        private set => Set(ref _isAnalyzing, value);
    }

    public bool IsInstallingDependencies
    {
        get => _isInstallingDependencies;
        private set => Set(ref _isInstallingDependencies, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string DependencyStatus
    {
        get => _dependencyStatus;
        private set => Set(ref _dependencyStatus, value);
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
                "正式版" => version.ReleaseType == "release",
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
            MissingRequiredCount = 0;
            UnresolvedRequiredCount = 0;
            DependencyStatus = "";
            return;
        }

        IsAnalyzing = true;
        DependencyStatus = "正在检测前置…";
        try
        {
            var items = await _analyzeDependencies(SelectedVersion, SelectedGameVersion, SelectedLoader);
            Dependencies.Clear();
            foreach (var item in items)
            {
                Dependencies.Add(item);
            }

            MissingRequiredCount = Dependencies.Count(item => item.Missing);
            UnresolvedRequiredCount = Dependencies.Count(item => item.UnresolvedRequired);
            OnPropertyChanged(nameof(HasDependencies));
            DependencyStatus = Dependencies.Count == 0
                ? "没有前置模组"
                : $"共 {Dependencies.Count} 个前置 · 缺少 {MissingRequiredCount} 个必需" +
                  (UnresolvedRequiredCount > 0 ? $" · {UnresolvedRequiredCount} 个无法解析" : "");
        }
        catch (Exception ex)
        {
            Dependencies.Clear();
            MissingRequiredCount = 0;
            UnresolvedRequiredCount = 0;
            OnPropertyChanged(nameof(HasDependencies));
            DependencyStatus = $"前置检测失败：{ex.Message}";
        }
        finally
        {
            IsAnalyzing = false;
        }
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
