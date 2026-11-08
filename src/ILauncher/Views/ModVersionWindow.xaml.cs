using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ILauncher.Core.Services;

namespace ILauncher.Views;

public partial class ModVersionWindow : Window, INotifyPropertyChanged
{
    private readonly Func<string, Task<IReadOnlyList<ModrinthVersion>>> _loadVersions;
    private string _selectedGameVersion;
    private ModrinthVersion? _selectedVersion;
    private bool _isLoading;
    private string _statusText = "";
    private bool _loaded;

    public ModVersionWindow(ModrinthProject project, string initialGameVersion,
        IReadOnlyList<string> gameVersions, string? loader,
        Func<string, Task<IReadOnlyList<ModrinthVersion>>> loadVersions)
    {
        Project = project;
        Loader = loader;
        GameVersions = gameVersions;
        _selectedGameVersion = initialGameVersion;
        _loadVersions = loadVersions;
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
    }

    public ModrinthProject Project { get; }
    public string? Loader { get; }
    public IReadOnlyList<string> GameVersions { get; }
    public ObservableCollection<ModrinthVersion> Versions { get; } = new();

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

    public ModrinthVersion? SelectedVersion
    {
        get => _selectedVersion;
        set => Set(ref _selectedVersion, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => Set(ref _isLoading, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

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
        try
        {
            var versions = await _loadVersions(gameVersion);
            Versions.Clear();
            foreach (var version in versions)
            {
                Versions.Add(version);
            }

            SelectedVersion = Versions.FirstOrDefault();
            StatusText = Versions.Count == 0
                ? "没有适用于该版本和加载器的文件"
                : $"共 {Versions.Count} 个文件版本";
        }
        catch (Exception ex)
        {
            Versions.Clear();
            SelectedVersion = null;
            StatusText = $"获取失败：{ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
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
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
