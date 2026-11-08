using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows.Data;
using System.Windows.Threading;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.Models;
using Microsoft.Win32;

namespace ILauncher.ViewModels;

public sealed class ModItemViewModel : ObservableObject
{
    private bool _enabled;
    private bool _isBusy;
    private bool _updateAvailable;
    private string _latestVersion = "";
    private ModrinthVersion? _latest;

    public required ModInfo Model { get; init; }

    public string Name => Model.DisplayName;
    public string Version => Model.Version;
    public string FileName => Model.FileName;
    public string SizeText => Model.SizeText;
    public string Loader => Model.Loader;
    public string ModId => Model.ModId;
    public byte[]? IconData => Model.IconData;
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    public bool UpdateAvailable
    {
        get => _updateAvailable;
        set
        {
            if (SetProperty(ref _updateAvailable, value))
            {
                OnPropertyChanged(nameof(UpdateText));
            }
        }
    }

    public string LatestVersion
    {
        get => _latestVersion;
        set
        {
            if (SetProperty(ref _latestVersion, value))
            {
                OnPropertyChanged(nameof(UpdateText));
            }
        }
    }

    public ModrinthVersion? LatestVersionInfo
    {
        get => _latest;
        set => SetProperty(ref _latest, value);
    }

    public string UpdateText => string.IsNullOrWhiteSpace(LatestVersion) ? "可更新" : $"可更新 v{LatestVersion}";

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (SetProperty(ref _enabled, value))
            {
                OnPropertyChanged(nameof(StateText));
            }
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetProperty(ref _isBusy, value);
    }

    public string StateText => Enabled ? "已启用" : "已禁用";

    public void RefreshBindings()
    {
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(Version));
        OnPropertyChanged(nameof(FileName));
        OnPropertyChanged(nameof(SizeText));
        OnPropertyChanged(nameof(DetailLine));
        OnPropertyChanged(nameof(StateText));
    }

    public string DetailLine => string.Join("  ·  ", new[]
    {
        string.IsNullOrWhiteSpace(Version) ? null : $"v{Version}",
        string.IsNullOrWhiteSpace(Loader) ? null : Loader,
        SizeText
    }.Where(part => !string.IsNullOrWhiteSpace(part)));
}

public sealed class ModsViewModel : ObservableObject
{
    private readonly ModService _modService;
    private readonly ModrinthService _modrinth;
    private readonly Action<string, bool> _toast;
    private readonly Action? _modsChanged;
    private readonly DispatcherTimer _searchTimer;
    private GameInstance? _instance;
    private string _searchText = "";
    private string _searchLower = "";
    private string _activeFilter = "all";
    private bool _isLoading;
    private bool _isCheckingUpdates;
    private int _enabledCount;
    private int _disabledCount;
    private Task _loadTask = Task.CompletedTask;

    public ModsViewModel(ModService modService, ModrinthService modrinth, Action<string, bool> toast, Action? modsChanged = null)
    {
        _modService = modService;
        _modrinth = modrinth;
        _toast = toast;
        _modsChanged = modsChanged;

        ModsView = CollectionViewSource.GetDefaultView(Mods);
        ModsView.Filter = FilterMod;

        FilterAll = new FilterItem { Title = "全部", Key = "all", IsSelected = true };
        FilterEnabled = new FilterItem { Title = "启用", Key = "enabled" };
        FilterDisabled = new FilterItem { Title = "禁用", Key = "disabled" };
        Filters = new[] { FilterAll, FilterEnabled, FilterDisabled };

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(160) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _searchLower = SearchText.Trim().ToLowerInvariant();
            ModsView.Refresh();
            OnPropertyChanged(nameof(IsEmpty));
        };

        RefreshCommand = new AsyncRelayCommand(LoadAsync, () => _instance != null);
        ToggleCommand = new AsyncRelayCommand(async parameter =>
        {
            if (parameter is ModItemViewModel item)
            {
                await ToggleAsync(item);
            }
        });
        OpenFolderCommand = new RelayCommand(OpenFolder, () => _instance != null);
        InstallCommand = new RelayCommand(Install, () => _instance != null);
        SetFilterCommand = new RelayCommand(SetFilter);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, () => _instance != null && !IsCheckingUpdates);
        UpdateModCommand = new AsyncRelayCommand(async parameter =>
        {
            if (parameter is ModItemViewModel item)
            {
                await UpdateModAsync(item);
            }
        });
    }

    public ObservableCollection<ModItemViewModel> Mods { get; } = new();

    public ICollectionView ModsView { get; }

    public FilterItem FilterAll { get; }
    public FilterItem FilterEnabled { get; }
    public FilterItem FilterDisabled { get; }
    public IEnumerable<FilterItem> Filters { get; }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand SetFilterCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public AsyncRelayCommand UpdateModCommand { get; }

    public bool IsCheckingUpdates
    {
        get => _isCheckingUpdates;
        private set
        {
            if (SetProperty(ref _isCheckingUpdates, value))
            {
                CheckUpdatesCommand.RaiseCanExecuteChanged();
                OnPropertyChanged(nameof(CheckUpdatesText));
            }
        }
    }

    public string CheckUpdatesText => IsCheckingUpdates ? "正在检查更新…" : "检查更新";

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }
    }

    public bool IsEmpty => Mods.Count == 0;

    public int EnabledCount
    {
        get => _enabledCount;
        private set
        {
            if (SetProperty(ref _enabledCount, value))
            {
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public int DisabledCount
    {
        get => _disabledCount;
        private set
        {
            if (SetProperty(ref _disabledCount, value))
            {
                OnPropertyChanged(nameof(SummaryText));
            }
        }
    }

    public string SummaryText => $"共 {Mods.Count} 个模组 · {EnabledCount} 启用 · {DisabledCount} 禁用";

    public string FolderPath => _instance?.ModsDirectory ?? "";

    public void Attach(GameInstance instance)
    {
        if (_instance?.Id == instance.Id && Mods.Count > 0)
        {
            return;
        }

        _instance = instance;
        OnPropertyChanged(nameof(FolderPath));
        RefreshCommand.RaiseCanExecuteChanged();
        OpenFolderCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        CheckUpdatesCommand.RaiseCanExecuteChanged();
        _ = LoadAsync();
    }

    public Task LoadAsync()
    {
        if (_instance == null)
        {
            return Task.CompletedTask;
        }

        if (!_loadTask.IsCompleted)
        {
            return _loadTask;
        }

        _loadTask = LoadCoreAsync();
        return _loadTask;
    }

    private async Task LoadCoreAsync()
    {
        var instance = _instance;
        if (instance == null)
        {
            return;
        }

        IsLoading = true;
        try
        {
            var models = await _modService.LoadModsAsync(instance);
            Mods.Clear();
            foreach (var model in models)
            {
                Mods.Add(new ModItemViewModel
                {
                    Model = model,
                    Enabled = model.Enabled
                });
            }

            RecalculateCounts();
            ModsView.Refresh();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(SummaryText));
        }
        catch (Exception ex)
        {
            _toast($"读取模组失败：{ex.Message}", true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task ToggleAsync(ModItemViewModel item)
    {
        if (item.IsBusy)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            await _modService.ToggleAsync(item.Model, !item.Enabled);
            item.Enabled = item.Model.Enabled;
            item.RefreshBindings();
            RecalculateCounts();
            ModsView.Refresh();
            OnPropertyChanged(nameof(IsEmpty));
            _modsChanged?.Invoke();
            _toast($"{item.Name} 已{(item.Enabled ? "启用" : "禁用")}，将在下次启动时生效。", false);
        }
        catch (Exception ex)
        {
            _toast($"操作失败：{ex.Message}", true);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private void RecalculateCounts()
    {
        EnabledCount = Mods.Count(m => m.Enabled);
        DisabledCount = Mods.Count - EnabledCount;
        FilterAll.Count = Mods.Count;
        FilterEnabled.Count = EnabledCount;
        FilterDisabled.Count = DisabledCount;
        OnPropertyChanged(nameof(SummaryText));
    }

    private bool FilterMod(object item)
    {
        if (item is not ModItemViewModel mod)
        {
            return false;
        }

        if (_activeFilter == "enabled" && !mod.Enabled)
        {
            return false;
        }

        if (_activeFilter == "disabled" && mod.Enabled)
        {
            return false;
        }

        if (string.IsNullOrEmpty(_searchLower))
        {
            return true;
        }

        return mod.Name.Contains(_searchLower, StringComparison.OrdinalIgnoreCase)
               || mod.FileName.Contains(_searchLower, StringComparison.OrdinalIgnoreCase)
               || mod.ModId.Contains(_searchLower, StringComparison.OrdinalIgnoreCase)
               || mod.Version.Contains(_searchLower, StringComparison.OrdinalIgnoreCase);
    }

    private void SetFilter(object? parameter)
    {
        var key = parameter as string ?? "all";
        if (_activeFilter == key)
        {
            return;
        }

        _activeFilter = key;
        FilterAll.IsSelected = key == "all";
        FilterEnabled.IsSelected = key == "enabled";
        FilterDisabled.IsSelected = key == "disabled";
        ModsView.Refresh();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private async Task CheckUpdatesAsync()
    {
        if (_instance == null || IsCheckingUpdates)
        {
            return;
        }

        IsCheckingUpdates = true;
        var available = 0;
        try
        {
            var loader = LoaderKey(_instance.Loader);
            foreach (var item in Mods.Where(mod => mod.Enabled))
            {
                item.UpdateAvailable = false;
                item.LatestVersionInfo = null;
                item.LatestVersion = "";
                try
                {
                    var path = item.Model.FilePath;
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    var sha1 = await Task.Run(() =>
                    {
                        using var stream = File.OpenRead(path);
                        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
                    });

                    var current = await _modrinth.GetVersionByHashAsync(sha1);
                    if (current == null)
                    {
                        continue;
                    }

                    var versions = await _modrinth.GetVersionsAsync(current.ProjectId, _instance.MinecraftVersion, loader);
                    var latest = versions.FirstOrDefault(version => version.File != null);
                    if (latest?.File == null)
                    {
                        continue;
                    }

                    if (string.Equals(latest.File.Sha1, sha1, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(latest.VersionNumber, item.Version, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    item.LatestVersion = latest.VersionNumber;
                    item.LatestVersionInfo = latest;
                    item.UpdateAvailable = true;
                    available++;
                }
                catch
                {
                    // Individual mod update checks are best-effort.
                }
            }

            _toast(available == 0
                ? "已检查完成，没有发现可更新模组。"
                : $"发现 {available} 个可更新模组，点击行内「更新」按钮升级。", false);
        }
        finally
        {
            IsCheckingUpdates = false;
            ModsView.Refresh();
        }
    }

    private async Task UpdateModAsync(ModItemViewModel item)
    {
        if (_instance == null || item.LatestVersionInfo?.File == null)
        {
            return;
        }

        item.IsBusy = true;
        try
        {
            var file = item.LatestVersionInfo.File;
            var fileName = string.Join("_", file.FileName.Split(Path.GetInvalidFileNameChars()));
            var target = Path.Combine(_instance.ModsDirectory, fileName);
            if (File.Exists(target) && !string.Equals(target, item.Model.FilePath, StringComparison.OrdinalIgnoreCase))
            {
                var name = Path.GetFileNameWithoutExtension(fileName);
                target = Path.Combine(_instance.ModsDirectory, $"{name} ({DateTime.Now:HHmmss}).jar");
            }

            var oldPath = item.Model.FilePath;
            var tempTarget = target + "." + Guid.NewGuid().ToString("N") + ".download";
            try
            {
                // Download first; only disable the old file once the new one is complete.
                await DownloadHelper.DownloadFileAsync(file.Url, tempTarget, null, CancellationToken.None);

                if (File.Exists(oldPath) && oldPath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    var backup = oldPath + ".disabled";
                    var index = 2;
                    while (File.Exists(backup))
                    {
                        backup = $"{oldPath}.{index++}.disabled";
                    }

                    File.Move(oldPath, backup);
                }

                File.Move(tempTarget, target, overwrite: true);
            }
            finally
            {
                if (File.Exists(tempTarget))
                {
                    File.Delete(tempTarget);
                }
            }

            _toast($"{item.Name} 已更新到 v{item.LatestVersion}，旧版本已禁用。", false);
            await LoadAsync();
            _modsChanged?.Invoke();
        }
        catch (Exception ex)
        {
            _toast($"更新失败：{ex.Message}", true);
        }
        finally
        {
            item.IsBusy = false;
        }
    }

    private static string? LoaderKey(string loader) => loader switch
    {
        "Fabric" => "fabric",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        "Quilt" => "quilt",
        _ => null
    };

    private void OpenFolder()
    {        if (_instance == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(_instance.ModsDirectory);
            Process.Start(new ProcessStartInfo(_instance.ModsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast($"无法打开文件夹：{ex.Message}", true);
        }
    }

    private void Install()
    {
        if (_instance == null)
        {
            return;
        }

        var dialog = new OpenFileDialog
        {
            Title = "选择要安装的 Mod",
            Filter = "Minecraft Mod (*.jar)|*.jar",
            Multiselect = true
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        _ = InstallFilesAsync(dialog.FileNames);
    }

    private async Task InstallFilesAsync(string[] files)
    {
        try
        {
            var count = await _modService.InstallAsync(files, _instance!.ModsDirectory);
            if (count > 0)
            {
                _toast($"已安装 {count} 个 Mod 到版本目录。", false);
                await LoadAsync();
            }
        }
        catch (Exception ex)
        {
            _toast($"安装失败：{ex.Message}", true);
        }
    }
}
