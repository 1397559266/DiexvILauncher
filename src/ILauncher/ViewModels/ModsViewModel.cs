using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Data;
using System.Windows.Threading;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.Localization;
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

    private string? _chineseName;

    /// <summary>MC百科匹配到的中文名，存在时优先显示。</summary>
    public string? ChineseName
    {
        get => _chineseName;
        set
        {
            if (SetProperty(ref _chineseName, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
                OnPropertyChanged(nameof(HasChineseName));
            }
        }
    }

    public bool HasChineseName => !string.IsNullOrWhiteSpace(_chineseName);

    public string? McmodUrl { get; set; }

    public string DisplayTitle => string.IsNullOrWhiteSpace(_chineseName) ? Name : _chineseName;
    public string Version => Model.Version;
    public string FileName => Model.FileName;
    public string SizeText => Model.SizeText;
    public string Loader => Model.Loader;
    public string ModId => Model.ModId;
    public byte[]? IconData => Model.IconData;
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1].ToUpperInvariant();

    private string? _remoteIconUrl;

    /// <summary>Modrinth / CurseForge 上的项目图标，优先于 jar 内图标显示。</summary>
    public string? RemoteIconUrl
    {
        get => _remoteIconUrl;
        set
        {
            if (SetProperty(ref _remoteIconUrl, value))
            {
                OnPropertyChanged(nameof(HasRemoteIcon));
                OnPropertyChanged(nameof(HasLocalIcon));
            }
        }
    }

    private bool _isSelected;

    /// <summary>多选状态，用于批量启用/禁用/删除。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public bool HasRemoteIcon => !string.IsNullOrWhiteSpace(_remoteIconUrl);
    public bool HasLocalIcon => !HasRemoteIcon;

    /// <summary>该文件在最近一次检查时的 SHA-1，用于命中更新缓存。</summary>
    public string CachedSha1 { get; set; } = "";

    /// <summary>缓存里记录的最新版本 id，用于按需补拉版本详情。</summary>
    public string CachedLatestVersionId { get; set; } = "";

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
    private bool _isLinkInstalling;
    private string _linkUrl = "";
    private string _linkInstallStatus = "";
    private int _enabledCount;
    private int _disabledCount;
    private readonly ModUpdateService _updates = new();
    private readonly McmodService _mcmod = new();
    private bool _chineseNamesStarted;
    private readonly Dictionary<string, ModUpdateState> _updateStates = new(StringComparer.OrdinalIgnoreCase);
    private bool _hasUpdates;
    private bool _updateCheckStarted;
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
        FilterEnabled = new FilterItem { Title = "已启用", Key = "enabled" };
        FilterDisabled = new FilterItem { Title = "已禁用", Key = "disabled" };
        FilterUpdates = new FilterItem { Title = "可更新", Key = "updates" };
        Filters = new ObservableCollection<FilterItem> { FilterAll, FilterEnabled, FilterDisabled };

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
        InstallFromLinkCommand = new AsyncRelayCommand(InstallFromLinkAsync, () => _instance != null && !IsLinkInstalling);
        SetFilterCommand = new RelayCommand(SetFilter);
        CheckUpdatesCommand = new AsyncRelayCommand(CheckUpdatesAsync, () => _instance != null && !IsCheckingUpdates);
        UpdateModCommand = new AsyncRelayCommand(async parameter =>
        {
            if (parameter is ModItemViewModel item)
            {
                await UpdateModAsync(item);
            }
        });
        SelectAllCommand = new RelayCommand(SelectAll);
        ClearSelectionCommand = new RelayCommand(ClearSelection);
        EnableSelectedCommand = new AsyncRelayCommand(() => SetSelectedEnabledAsync(true), () => HasSelection);
        DisableSelectedCommand = new AsyncRelayCommand(() => SetSelectedEnabledAsync(false), () => HasSelection);
        DeleteSelectedCommand = new AsyncRelayCommand(DeleteSelectedAsync, () => HasSelection);
    }

    public ObservableCollection<ModItemViewModel> Mods { get; } = new();

    public ICollectionView ModsView { get; }

    public FilterItem FilterAll { get; }
    public FilterItem FilterEnabled { get; }
    public FilterItem FilterDisabled { get; }
    public FilterItem FilterUpdates { get; }
    public ObservableCollection<FilterItem> Filters { get; }

    /// <summary>只有检测到可更新模组时，筛选栏里才出现「可更新」。</summary>
    public bool HasUpdates
    {
        get => _hasUpdates;
        private set => SetProperty(ref _hasUpdates, value);
    }

    public AsyncRelayCommand RefreshCommand { get; }
    public AsyncRelayCommand ToggleCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand InstallCommand { get; }
    public AsyncRelayCommand InstallFromLinkCommand { get; }
    public RelayCommand SetFilterCommand { get; }
    public AsyncRelayCommand CheckUpdatesCommand { get; }
    public AsyncRelayCommand UpdateModCommand { get; }
    public RelayCommand SelectAllCommand { get; }
    public RelayCommand ClearSelectionCommand { get; }
    public AsyncRelayCommand EnableSelectedCommand { get; }
    public AsyncRelayCommand DisableSelectedCommand { get; }
    public AsyncRelayCommand DeleteSelectedCommand { get; }

    public int SelectedCount => Mods.Count(m => m.IsSelected);

    public bool HasSelection => SelectedCount > 0;

    public string SelectionText => $"已选择 {SelectedCount} 个模组";

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

    public bool IsLinkInstalling
    {
        get => _isLinkInstalling;
        private set
        {
            if (SetProperty(ref _isLinkInstalling, value))
            {
                InstallFromLinkCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string LinkUrl
    {
        get => _linkUrl;
        set => SetProperty(ref _linkUrl, value);
    }

    public string LinkInstallStatus
    {
        get => _linkInstallStatus;
        private set => SetProperty(ref _linkInstallStatus, value);
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

    public bool IsEmpty => !IsLoading && Mods.Count == 0;

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
        InstallFromLinkCommand.RaiseCanExecuteChanged();
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
                var item = new ModItemViewModel
                {
                    Model = model,
                    Enabled = model.Enabled
                };
                item.PropertyChanged += OnSelectionChanged;
                Mods.Add(item);
            }

            RecalculateCounts();
            _ = ApplyCachedUpdatesAsync();
            RecalculateCounts();
            ModsView.Refresh();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(SummaryText));
            _ = AutoCheckUpdatesAsync();
            _ = EnrichChineseNamesAsync();
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

        var updates = Mods.Count(m => m.UpdateAvailable);
        FilterUpdates.Count = updates;
        HasUpdates = updates > 0;
        if (updates > 0)
        {
            if (!Filters.Contains(FilterUpdates))
            {
                Filters.Add(FilterUpdates);
            }
        }
        else
        {
            Filters.Remove(FilterUpdates);
            if (_activeFilter == "updates")
            {
                _activeFilter = "all";
                FilterAll.IsSelected = true;
                FilterUpdates.IsSelected = false;
            }
        }

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

        if (_activeFilter == "updates" && !mod.UpdateAvailable)
        {
            return false;
        }

        if (string.IsNullOrEmpty(_searchLower))
        {
            return true;
        }

        return mod.Name.Contains(_searchLower, StringComparison.OrdinalIgnoreCase)
               || (mod.ChineseName?.Contains(_searchLower, StringComparison.OrdinalIgnoreCase) ?? false)
               || mod.FileName.Contains(_searchLower, StringComparison.OrdinalIgnoreCase)
               || mod.ModId.Contains(_searchLower, StringComparison.OrdinalIgnoreCase)
               || mod.Version.Contains(_searchLower, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>拖拽 .jar 到模组页时调用：复制到当前版本的 mods 目录并刷新列表。</summary>
    public async Task InstallDroppedAsync(IReadOnlyList<string> files)
    {
        if (_instance == null)
        {
            _toast("请先选择一个游戏版本。", true);
            return;
        }

        var jars = files.Where(file => file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)).ToList();
        if (jars.Count == 0)
        {
            _toast("只支持 .jar 模组文件。", true);
            return;
        }

        try
        {
            // 同名文件（通常是同一个模组）直接跳过，避免拖两次就多出一个副本。
            var existing = new HashSet<string>(
                Directory.EnumerateFiles(_instance.ModsDirectory).Select(path => Path.GetFileName(path)),
                StringComparer.OrdinalIgnoreCase);
            var toCopy = new List<string>();
            var skipped = 0;
            foreach (var jar in jars)
            {
                if (existing.Contains(Path.GetFileName(jar)))
                {
                    skipped++;
                }
                else
                {
                    toCopy.Add(jar);
                }
            }

            if (toCopy.Count == 0)
            {
                _toast($"{skipped} 个模组已存在（同名），没有覆盖。", true);
                return;
            }

            var copied = await _modService.InstallAsync(toCopy, _instance.ModsDirectory);
            await LoadAsync();
            _modsChanged?.Invoke();
            var skippedText = skipped > 0 ? $"，跳过 {skipped} 个同名文件" : "";
            _toast($"已安装 {copied} 个模组{skippedText}，重启游戏后生效。", false);
        }
        catch (Exception ex)
        {
            _toast($"安装失败：{ex.Message}", true);
        }
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
        try
        {
            var available = await RunUpdateCheckAsync();
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

    /// <summary>
    /// 批量识别本地模组并查询最新版本：一次哈希接口 + 一次项目接口 + 每个项目一次版本查询，
    /// 顺便把 Modrinth / CurseForge 的项目图标取回来。
    /// </summary>
    private async Task<int> RunUpdateCheckAsync()
    {
        if (_instance == null)
        {
            return 0;
        }

        var loader = LoaderKey(_instance.Loader);
        var candidates = new List<(ModItemViewModel Item, string Sha1)>();
        foreach (var item in Mods)
        {
            var path = item.Model.FilePath;
            if (!File.Exists(path))
            {
                continue;
            }

            item.UpdateAvailable = false;
            item.LatestVersion = "";
            item.LatestVersionInfo = null;

            var sha1 = await Task.Run(() => ModHashCache.GetSha1(path));
            item.CachedSha1 = sha1;
            candidates.Add((item, sha1));
        }

        if (candidates.Count == 0)
        {
            return 0;
        }

        var result = await _updates.CheckAsync(
            candidates.Select(c => (c.Sha1, c.Item.Model.FilePath, c.Item.Name, c.Item.Version)).ToList(),
            _instance.MinecraftVersion, loader);

        var available = 0;
        foreach (var (item, sha1) in candidates)
        {
            if (!result.TryGetValue(sha1, out var state))
            {
                continue;
            }

            _updateStates[sha1] = state;
            item.RemoteIconUrl = state.IconUrl;
            item.CachedLatestVersionId = state.LatestVersionId;
            if (!state.UpdateAvailable || !item.Enabled)
            {
                continue;
            }

            item.LatestVersion = state.LatestVersionNumber;
            item.UpdateAvailable = true;
            available++;
        }

        SaveUpdateCache(result);
        RecalculateCounts();
        ModsView.Refresh();
        return available;
    }

    private void OnSelectionChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(ModItemViewModel.IsSelected))
        {
            return;
        }

        OnPropertyChanged(nameof(SelectedCount));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(SelectionText));
        EnableSelectedCommand.RaiseCanExecuteChanged();
        DisableSelectedCommand.RaiseCanExecuteChanged();
        DeleteSelectedCommand.RaiseCanExecuteChanged();
    }

    private void SelectAll()
    {
        foreach (var item in ModsView.Cast<ModItemViewModel>())
        {
            item.IsSelected = true;
        }
    }

    private void ClearSelection()
    {
        foreach (var item in Mods)
        {
            item.IsSelected = false;
        }
    }

    /// <summary>批量启用/禁用：直接改文件名，和单行开关是同一套逻辑。</summary>
    private async Task SetSelectedEnabledAsync(bool enable)
    {
        var targets = Mods.Where(m => m.IsSelected && m.Enabled != enable).ToList();
        if (targets.Count == 0)
        {
            _toast("选中的模组已经处于该状态。", false);
            return;
        }

        foreach (var item in targets)
        {
            item.IsBusy = true;
            try
            {
                await _modService.ToggleAsync(item.Model, enable);
                item.Enabled = item.Model.Enabled;
                item.RefreshBindings();
            }
            catch (Exception ex)
            {
                _toast($"处理 {item.Name} 失败：{ex.Message}", true);
            }
            finally
            {
                item.IsBusy = false;
            }
        }

        RecalculateCounts();
        ModsView.Refresh();
        _modsChanged?.Invoke();
        _toast($"已{(enable ? "启用" : "禁用")} {targets.Count} 个模组，下次启动游戏时生效。", false);
    }

    /// <summary>批量删除：文件先移到回收目录，避免误删后无法恢复。</summary>
    private async Task DeleteSelectedAsync()
    {
        var targets = Mods.Where(m => m.IsSelected).ToList();
        if (targets.Count == 0)
        {
            return;
        }

        var confirm = LocalizedDialog.Show($"确定删除选中的 {targets.Count} 个模组文件？\n\n文件会移到回收目录，可手动恢复。",
            "删除模组", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK)
        {
            return;
        }

        var trash = Path.Combine(AppPaths.DataDirectory, "trash", "mods", DateTime.Now.ToString("yyyyMMdd-HHmmss"));
        var removed = 0;
        await Task.Run(() =>
        {
            Directory.CreateDirectory(trash);
            foreach (var item in targets)
            {
                try
                {
                    var source = item.Model.FilePath;
                    if (!File.Exists(source))
                    {
                        continue;
                    }

                    File.Move(source, Path.Combine(trash, Path.GetFileName(source)));
                    removed++;
                }
                catch
                {
                    // 单个文件失败不影响其余删除。
                }
            }
        });

        foreach (var item in targets)
        {
            if (!File.Exists(item.Model.FilePath))
            {
                Mods.Remove(item);
            }
        }

        RecalculateCounts();
        ModsView.Refresh();
        _modsChanged?.Invoke();
        _toast($"已删除 {removed} 个模组，文件在 {trash}。", false);
    }

    /// <summary>用 MC百科补全模组中文名；MC百科限流，按顺序慢慢查，结果有本地缓存。</summary>
    private async Task EnrichChineseNamesAsync()
    {
        if (_chineseNamesStarted || LanguageService.CurrentLanguage == LanguageService.English)
        {
            return;
        }

        _chineseNamesStarted = true;
        foreach (var item in Mods.ToList())
        {
            try
            {
                var info = await _mcmod.LookupAsync(item.Name);
                if (info == null || string.IsNullOrWhiteSpace(info.Name) ||
                    string.Equals(info.Name, item.Name, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                // 只有真的是中文名才覆盖，避免把英文名替换成另一个英文名。
                if (!info.Name.Any(c => c >= 0x4E00 && c <= 0x9FFF))
                {
                    continue;
                }

                item.ChineseName = info.Name;
                item.McmodUrl = info.Url;
            }
            catch
            {
                // 单个查询失败不影响其余模组。
            }
        }
    }

    private async Task AutoCheckUpdatesAsync()
    {
        if (_updateCheckStarted || _instance == null)
        {
            return;
        }

        _updateCheckStarted = true;
        var cache = LoadUpdateCache();
        if (cache.Count > 0 && (DateTime.Now - cache.Values.Max(v => v.CheckedAt)).TotalHours < 6)
        {
            return;
        }

        IsCheckingUpdates = true;
        try
        {
            await RunUpdateCheckAsync();
        }
        catch
        {
            // 自动检查失败时不打扰用户。
        }
        finally
        {
            IsCheckingUpdates = false;
        }
    }

    private static string UpdateCachePath => Path.Combine(AppPaths.CacheDirectory, "mod-updates.json");

    private static Dictionary<string, ModUpdateState> LoadUpdateCache()
    {
        try
        {
            if (!File.Exists(UpdateCachePath))
            {
                return new Dictionary<string, ModUpdateState>(StringComparer.OrdinalIgnoreCase);
            }

            var json = File.ReadAllText(UpdateCachePath);
            var data = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, ModUpdateState>>(json);
            return data == null
                ? new Dictionary<string, ModUpdateState>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, ModUpdateState>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, ModUpdateState>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private static void SaveUpdateCache(IReadOnlyDictionary<string, ModUpdateState> entries)
    {
        try
        {
            AppPaths.EnsureCreated();
            var json = System.Text.Json.JsonSerializer.Serialize(entries,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(UpdateCachePath, json);
        }
        catch
        {
            // 缓存写入失败不影响本次检查结果。
        }
    }

    /// <summary>进入模组管理时先把缓存里的“可更新”和远程图标贴上去，界面不需要等网络。</summary>
    private async Task ApplyCachedUpdatesAsync()
    {
        var cache = LoadUpdateCache();
        if (cache.Count == 0)
        {
            return;
        }

        var items = Mods.ToList();
        // 哈希在后台算，并命中 ModHashCache，避免进页面时读全部 jar 卡住 UI。
        var hashes = await Task.Run(() => items.ToDictionary(
            item => item,
            item => ModHashCache.GetSha1(item.Model.FilePath)));
        ModHashCache.SaveIfDirty();

        foreach (var item in items)
        {
            if (!hashes.TryGetValue(item, out var sha1) || string.IsNullOrEmpty(sha1))
            {
                continue;
            }

            item.CachedSha1 = sha1;
            if (!cache.TryGetValue(sha1, out var state))
            {
                continue;
            }

            _updateStates[sha1] = state;
            item.RemoteIconUrl = state.IconUrl;
            item.CachedLatestVersionId = state.LatestVersionId;
            if (!state.UpdateAvailable || !item.Enabled)
            {
                continue;
            }

            item.LatestVersion = state.LatestVersionNumber;
            item.UpdateAvailable = true;
        }

        RecalculateCounts();
        ModsView.Refresh();
    }
    private async Task UpdateModAsync(ModItemViewModel item)
    {
        if (_instance == null)
        {
            return;
        }

        if (item.LatestVersionInfo?.File == null && _updateStates.TryGetValue(item.CachedSha1, out var cached) &&
            !string.IsNullOrWhiteSpace(cached.LatestDownloadUrl))
        {
            item.LatestVersionInfo = new ModrinthVersion(
                cached.LatestVersionId,
                cached.ProjectId,
                cached.LatestVersionNumber,
                cached.LatestVersionNumber,
                "release",
                cached.CheckedAt,
                [],
                [],
                new ModrinthFile(cached.LatestDownloadUrl, cached.LatestFileName ?? "mod.jar", 0, true,
                    cached.LatestSha1),
                []);
            item.LatestVersion = cached.LatestVersionNumber;
        }
        else if (item.LatestVersionInfo?.File == null && !string.IsNullOrWhiteSpace(item.CachedLatestVersionId) &&
                 !item.CachedLatestVersionId.StartsWith("cf-", StringComparison.Ordinal))
        {
            try
            {
                item.LatestVersionInfo = await _modrinth.GetVersionAsync(item.CachedLatestVersionId);
                item.LatestVersion = item.LatestVersionInfo?.VersionNumber ?? item.LatestVersion;
            }
            catch
            {
                // 补拉失败就交给下面统一提示。
            }
        }

        if (item.LatestVersionInfo?.File == null)
        {
            _toast("没有拿到最新版本的文件信息，请点「检查更新」后重试。", true);
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
                await DownloadHelper.DownloadFileAsync(file.Url, tempTarget, null, CancellationToken.None, file.Sha1);

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

    private async Task InstallFromLinkAsync()
    {
        if (_instance == null)
        {
            return;
        }

        if (!Uri.TryCreate(LinkUrl.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            _toast("请输入有效的 http/https 链接。", true);
            return;
        }

        IsLinkInstalling = true;
        LinkInstallStatus = "正在解析链接…";
        try
        {
            if (uri.Host.Contains("modrinth.com", StringComparison.OrdinalIgnoreCase))
            {
                var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
                var marker = Array.FindIndex(segments, part => part is "mod" or "project");
                var slug = marker >= 0 && marker + 1 < segments.Length ? segments[marker + 1] : null;
                if (string.IsNullOrWhiteSpace(slug))
                {
                    throw new InvalidOperationException("无法从 Modrinth 链接中识别项目。");
                }

                var project = await _modrinth.GetProjectAsync(slug)
                              ?? throw new InvalidOperationException("Modrinth 项目不存在或无法访问。");
                var file = await _modrinth.InstallAsync(project, _instance.ModsDirectory,
                    _instance.MinecraftVersion, LoaderKey(_instance.Loader), null, CancellationToken.None);
                _toast($"已从链接安装 {Path.GetFileName(file)}。", false);
            }
            else if (uri.AbsolutePath.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                var fileName = Path.GetFileName(Uri.UnescapeDataString(uri.AbsolutePath));
                if (string.IsNullOrWhiteSpace(fileName))
                {
                    fileName = "mod.jar";
                }

                Directory.CreateDirectory(_instance.ModsDirectory);
                var target = Path.Combine(_instance.ModsDirectory, fileName);
                if (File.Exists(target))
                {
                    target = Path.Combine(_instance.ModsDirectory,
                        Path.GetFileNameWithoutExtension(fileName) + "-" + DateTime.Now.ToString("HHmmss") + ".jar");
                }

                LinkInstallStatus = $"正在下载 {fileName}…";
                await DownloadHelper.DownloadFileAsync(uri.AbsoluteUri, target, null, CancellationToken.None);
                _toast($"已从链接安装 {Path.GetFileName(target)}。", false);
            }
            else
            {
                throw new InvalidOperationException(
                    "CurseForge 项目页需要 API Key；请在浏览器打开项目页下载 .jar 后使用「从文件安装」，或粘贴文件直链。");
            }

            LinkUrl = "";
            await LoadAsync();
        }
        catch (Exception ex)
        {
            LinkInstallStatus = $"安装失败：{ex.Message}";
            _toast($"链接安装失败：{ex.Message}", true);
        }
        finally
        {
            IsLinkInstalling = false;
        }
    }

    private void Install()
    {
        if (_instance == null)
        {
            return;
        }

        var dialog = new OpenFileDialog { Multiselect = true };
        LocalizedDialog.Configure(dialog, "选择要安装的 Mod", "Minecraft Mod (*.jar)|*.jar");

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
