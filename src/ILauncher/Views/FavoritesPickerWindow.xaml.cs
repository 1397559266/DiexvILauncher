using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.ViewModels;

namespace ILauncher.Views;

/// <summary>从收藏列表中挑一个资源，下载到指定实例对应的目录（mods / shaderpacks / resourcepacks）。</summary>
public partial class FavoritesPickerWindow : Window, INotifyPropertyChanged
{
    private readonly GameInstance _instance;
    private readonly FavoritesService _favorites = new();
    private readonly FavoriteInstallerService _installer = new();
    private string _searchText = "";
    private string _selectedKind = "全部";
    private string _statusText = "";
    private bool _canInstall = true;

    public FavoritesPickerWindow(GameInstance instance)
    {
        InitializeComponent();
        _instance = instance;
        DataContext = this;
        TargetText = $"{instance.Id} · {instance.MinecraftVersion} · {instance.Loader}";
        InstallCommand = new RelayCommand(parameter => _ = InstallAsync(parameter as FavoriteEntry));
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FavoriteEntry> Items { get; } = new();

    public IReadOnlyList<string> KindFilters { get; } = new[] { "全部", "模组", "整合包", "光影包", "资源包" };

    public RelayCommand InstallCommand { get; }

    public string TargetText { get; }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value ?? "";
            Raise();
            Reload();
        }
    }

    public string SelectedKind
    {
        get => _selectedKind;
        set
        {
            if (_selectedKind == value)
            {
                return;
            }

            _selectedKind = value ?? "全部";
            Raise();
            Reload();
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            Raise();
        }
    }

    public bool CanInstall
    {
        get => _canInstall;
        private set
        {
            _canInstall = value;
            Raise();
        }
    }

    private void Reload()
    {
        var kind = SelectedKind switch
        {
            "模组" => FavoriteKinds.Mod,
            "整合包" => FavoriteKinds.Modpack,
            "光影包" => FavoriteKinds.Shader,
            "资源包" => FavoriteKinds.ResourcePack,
            _ => null
        };
        var keyword = SearchText.Trim();

        Items.Clear();
        foreach (var item in _favorites.All
                     .Where(f => kind == null || string.Equals(f.Kind, kind, StringComparison.OrdinalIgnoreCase))
                     .Where(f => keyword.Length == 0 || f.Title.Contains(keyword, StringComparison.OrdinalIgnoreCase)))
        {
            Items.Add(item);
        }

        StatusText = Items.Count == 0
            ? "没有匹配的收藏；可以先去下载页收藏几个项目。"
            : $"共 {Items.Count} 项收藏，选择后下载到 {_instance.Id}。";
    }

    private async Task InstallAsync(FavoriteEntry? entry)
    {
        if (entry == null || !CanInstall)
        {
            return;
        }

        CanInstall = false;
        try
        {
            var progress = new Progress<string>(text =>
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null || dispatcher.CheckAccess())
                {
                    StatusText = text;
                }
                else
                {
                    dispatcher.BeginInvoke(() => StatusText = text);
                }
            });

            var path = await _installer.InstallAsync(entry, _instance, progress);
            StatusText = $"已安装到 {Path.GetFileName(path)}。";
        }
        catch (Exception ex)
        {
            StatusText = $"安装失败：{ex.Message}";
        }
        finally
        {
            CanInstall = true;
        }
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
