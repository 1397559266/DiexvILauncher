using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
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
        InstallAllCommand = new AsyncRelayCommand(InstallAllModsAsync, () => CanInstall);
        Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FavoriteEntry> Items { get; } = new();

    public IReadOnlyList<string> KindFilters { get; } = new[] { "全部", "模组", "整合包", "光影包", "资源包" };

    public RelayCommand InstallCommand { get; }

    /// <summary>一键安装全部模组收藏。</summary>
    public AsyncRelayCommand InstallAllCommand { get; }

    private int _selectedCount;

    public string InstallSelectedText =>
        _selectedCount <= 0 ? "安装选中的" : $"安装选中的（{_selectedCount}）";

    public string InstallAllText
    {
        get
        {
            var count = Items.Count(item => !string.Equals(item.Kind, FavoriteKinds.Modpack, StringComparison.OrdinalIgnoreCase));
            return count == 0 ? "安装全部收藏" : $"安装全部收藏（{count}）";
        }
    }

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
            InstallAllCommand.RaiseCanExecuteChanged();
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
        Raise(nameof(InstallAllText));
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

    /// <summary>把收藏里的模组一次性下载到当前版本。</summary>
    private Task InstallAllModsAsync() => InstallBatchAsync(Items.ToList(), "模组");

    private void ItemList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _selectedCount = ItemList.SelectedItems.Count;
        Raise(nameof(InstallSelectedText));
        if (_selectedCount > 0)
        {
            StatusText = $"已选中 {_selectedCount} 项，点「安装选中的」下载到 {_instance.Id}。";
        }
        else
        {
            StatusText = $"共 {Items.Count} 项收藏，选择后下载到 {_instance.Id}。";
        }
    }

    private async void InstallSelected_Click(object sender, RoutedEventArgs e)
    {
        var items = ItemList.SelectedItems.OfType<FavoriteEntry>().ToList();
        if (items.Count == 0)
        {
            StatusText = "先点选要安装的收藏（按住 Ctrl 可多选）。";
            return;
        }

        await InstallBatchAsync(items, "收藏");
        ItemList.SelectedItems.Clear();
    }

    /// <summary>按顺序安装一批收藏，跳过不支持的整包类型。</summary>
    private async Task InstallBatchAsync(IReadOnlyList<FavoriteEntry> entries, string label)
    {
        if (!CanInstall)
        {
            return;
        }

        var installable = entries
            .Where(entry => !string.Equals(entry.Kind, FavoriteKinds.Modpack, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (installable.Count == 0)
        {
            StatusText = "选中的都是整合包，整合包请到下载页安装。";
            return;
        }

        CanInstall = false;
        var done = 0;
        var failed = 0;
        try
        {
            foreach (var entry in installable)
            {
                StatusText = $"正在安装 {entry.Title}（{done + failed + 1}/{installable.Count}）…";
                try
                {
                    await _installer.InstallAsync(entry, _instance, null);
                    done++;
                }
                catch
                {
                    failed++;
                }
            }

            StatusText = failed == 0
                ? $"已安装 {done} 项{label}，重启游戏后生效。"
                : $"安装完成：成功 {done} 项，失败 {failed} 项。";
        }
        finally
        {
            CanInstall = true;
        }
    }
}
