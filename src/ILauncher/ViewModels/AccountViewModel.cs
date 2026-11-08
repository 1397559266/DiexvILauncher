using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Input;
using ILauncher.Core;
using ILauncher.Core.Minecraft;
using ILauncher.Core.Services;
using ILauncher.Localization;
using ILauncher.Models;
using ILauncher.Services;
using ILauncher.Views;
using Microsoft.Win32;

namespace ILauncher.ViewModels;

public sealed class AccountViewModel : ObservableObject
{
    private readonly SettingsService _settings;
    private readonly SkinService _skins;
    private readonly MojangProfileService _mojang;
    private readonly Action<string, bool> _toast;
    private readonly Action? _saved;
    private string _playerName;
    private string _skinPath;
    private string _skinModel;
    private string _capePath;
    private string _profileLookup = "";
    private string _profileStatus = "输入正版玩家名或 UUID，可获取该玩家的皮肤外观（只查询皮肤，不登录账号）。";
    private string _profileUuid = "";
    private bool _isLookingUp;
    private bool _autoRotate = true;
    private SkinEntry? _selectedSkin;

    public AccountViewModel(SettingsService settings, SkinService skins, MojangProfileService mojang,
        Action<string, bool> toast, Action? saved = null)
    {
        _settings = settings;
        _skins = skins;
        _mojang = mojang;
        _toast = toast;
        _saved = saved;

        _playerName = settings.ResolvePlayerName();
        _skinPath = skins.Resolve(settings.Settings.SkinPath);
        _skinModel = string.IsNullOrWhiteSpace(settings.Settings.SkinModel) ? "Steve" : settings.Settings.SkinModel;
        _capePath = settings.Settings.CapePath;
        _profileUuid = settings.Settings.ProfileUuid;
        if (string.Equals(settings.Settings.AccountType, "微软账户", StringComparison.Ordinal))
        {
            settings.Settings.MicrosoftAccessToken = Unprotect(settings.Settings.MicrosoftAccessTokenProtected);
            settings.Settings.MicrosoftRefreshToken = Unprotect(settings.Settings.MicrosoftRefreshTokenProtected);
        }

        Skins = new ObservableCollection<SkinEntry>();
        SaveCommand = new RelayCommand(Save);
        ImportSkinCommand = new RelayCommand(ImportSkin);
        UseDefaultCommand = new RelayCommand(UseDefault);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        LookupCommand = new AsyncRelayCommand(LookupAsync, () => !IsLookingUp && !string.IsNullOrWhiteSpace(ProfileLookup));
        MicrosoftLoginCommand = new AsyncRelayCommand(MicrosoftLoginAsync);
        SignOutMicrosoftCommand = new RelayCommand(SignOutMicrosoft);
        SelectSkinCommand = new RelayCommand(parameter =>
        {
            if (parameter is SkinEntry entry)
            {
                SelectedSkin = entry;
            }
        });
        SetModelCommand = new RelayCommand(parameter =>
        {
            if (parameter is string model)
            {
                SkinModel = model;
                PersistSkinSelection();
            }
        });
        RefreshLibraryCommand = new RelayCommand(LoadLibrary);

        LoadLibrary();
    }

    public ObservableCollection<SkinEntry> Skins { get; }

    public ICommand SaveCommand { get; }
    public RelayCommand ImportSkinCommand { get; }
    public RelayCommand UseDefaultCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public AsyncRelayCommand LookupCommand { get; }
    public AsyncRelayCommand MicrosoftLoginCommand { get; }
    public RelayCommand SignOutMicrosoftCommand { get; }
    public RelayCommand SelectSkinCommand { get; }
    public RelayCommand SetModelCommand { get; }
    public RelayCommand RefreshLibraryCommand { get; }

    public string PlayerName
    {
        get => _playerName;
        set
        {
            if (SetProperty(ref _playerName, value))
            {
                OnPropertyChanged(nameof(AccountTypeText));
                OnPropertyChanged(nameof(UuidText));
            }
        }
    }

    public string SkinPath
    {
        get => _skinPath;
        private set
        {
            if (SetProperty(ref _skinPath, value))
            {
                OnPropertyChanged(nameof(SkinName));
            }
        }
    }

    public string SkinModel
    {
        get => _skinModel;
        private set
        {
            if (!SetProperty(ref _skinModel, value))
            {
                return;
            }

            OnPropertyChanged(nameof(IsSteve));
            OnPropertyChanged(nameof(IsAlex));
            OnPropertyChanged(nameof(ModelText));
        }
    }

    public bool IsSteve => !string.Equals(SkinModel, "Alex", StringComparison.OrdinalIgnoreCase);
    public bool IsAlex => string.Equals(SkinModel, "Alex", StringComparison.OrdinalIgnoreCase);
    public string ModelText => IsAlex ? "Alex（纤细）" : "Steve（经典）";

    public string CapePath
    {
        get => _capePath;
        private set => SetProperty(ref _capePath, value);
    }

    public SkinEntry? SelectedSkin
    {
        get => _selectedSkin;
        set
        {
            if (!SetProperty(ref _selectedSkin, value) || value == null)
            {
                return;
            }

            foreach (var entry in Skins)
            {
                entry.IsSelected = ReferenceEquals(entry, value);
            }

            SkinPath = value.Path;
            SkinModel = value.Model;
            CapePath = _skins.FindCapeFor(value.Path) ?? "";
            PersistSkinSelection();
        }
    }

    public string SkinName => string.IsNullOrWhiteSpace(SkinPath) ? "Steve" : Path.GetFileNameWithoutExtension(SkinPath);

    public string AccountTypeText =>
        LanguageService.Translate(IsMicrosoftAccount
            ? "微软账户 · 正版登录"
            : "离线账户 · 本地启动");

    public bool IsMicrosoftAccount =>
        string.Equals(_settings.Settings.AccountType, "微软账户", StringComparison.Ordinal) &&
        (!string.IsNullOrWhiteSpace(_settings.Settings.MicrosoftAccessToken) ||
         !string.IsNullOrWhiteSpace(_settings.Settings.MicrosoftRefreshToken));

    public string UuidText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_profileUuid))
            {
                return MojangProfileService.NormalizeUuid(_profileUuid);
            }

            return CreateOfflineUuid(PlayerName);
        }
    }

    public string ProfileLookup
    {
        get => _profileLookup;
        set
        {
            if (SetProperty(ref _profileLookup, value))
            {
                LookupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public string ProfileStatus
    {
        get => _profileStatus;
        private set => SetProperty(ref _profileStatus, value);
    }

    public bool IsLookingUp
    {
        get => _isLookingUp;
        private set
        {
            if (SetProperty(ref _isLookingUp, value))
            {
                LookupCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool AutoRotate
    {
        get => _autoRotate;
        set => SetProperty(ref _autoRotate, value);
    }

    public void LoadLibrary()
    {
        var selected = SkinPath;
        Skins.Clear();
        foreach (var entry in _skins.LoadLibrary(selected))
        {
            Skins.Add(entry);
        }

        _selectedSkin = Skins.FirstOrDefault(s => s.IsSelected) ?? Skins.FirstOrDefault();
        OnPropertyChanged(nameof(SelectedSkin));
    }

    private async Task MicrosoftLoginAsync()
    {
        var window = new MicrosoftLoginWindow(_settings) { Owner = Application.Current?.MainWindow };
        if (window.ShowDialog() != true || window.Account == null)
        {
            return;
        }

        var account = window.Account;
        PlayerName = account.Name;
        _profileUuid = account.Uuid;
        _settings.Settings.AccountType = "微软账户";
        _settings.Settings.MicrosoftAccessToken = account.AccessToken;
        _settings.Settings.MicrosoftRefreshToken = account.RefreshToken;
        _settings.Settings.MicrosoftUuid = account.Uuid;
        _settings.Settings.MicrosoftAccessTokenProtected = Protect(account.AccessToken);
        _settings.Settings.MicrosoftRefreshTokenProtected = Protect(account.RefreshToken);
        _settings.Settings.ProfileUuid = account.Uuid;
        _settings.Save();

        if (!string.IsNullOrWhiteSpace(account.SkinUrl))
        {
            try
            {
                var bytes = await DownloadHelper.GetBytesAsync(account.SkinUrl, CancellationToken.None);
                var path = Path.Combine(_skins.SkinsDirectory,
                    $"{account.Name}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
                await File.WriteAllBytesAsync(path, bytes);
                SkinPath = path;
                SkinModel = "Steve";
            }
            catch
            {
                // The account is still usable if the skin download fails.
            }
        }

        Save();
        OnPropertyChanged(nameof(AccountTypeText));
        OnPropertyChanged(nameof(IsMicrosoftAccount));
        _toast("微软账户登录成功。", false);
    }

    private void SignOutMicrosoft()
    {
        if (!IsMicrosoftAccount)
        {
            return;
        }

        _settings.Settings.AccountType = "离线账户";
        _settings.Settings.MicrosoftAccessToken = "";
        _settings.Settings.MicrosoftRefreshToken = "";
        _settings.Settings.MicrosoftUuid = "";
        _settings.Settings.MicrosoftAccessTokenProtected = "";
        _settings.Settings.MicrosoftRefreshTokenProtected = "";
        _settings.Settings.ProfileUuid = "";
        _settings.Save();

        _profileUuid = "";
        PlayerName = _settings.ResolvePlayerName();
        OnPropertyChanged(nameof(AccountTypeText));
        OnPropertyChanged(nameof(IsMicrosoftAccount));
        OnPropertyChanged(nameof(UuidText));
        _saved?.Invoke();
        _toast("已注销微软账号，当前为离线账户。", false);
    }

    private static string Protect(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return "";
        }

        try
        {
            return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null,
                DataProtectionScope.CurrentUser));
        }
        catch
        {
            return "";
        }
    }

    private static string Unprotect(string protectedToken)
    {
        if (string.IsNullOrWhiteSpace(protectedToken))
        {
            return "";
        }

        try
        {
            var bytes = ProtectedData.Unprotect(Convert.FromBase64String(protectedToken), null,
                DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            return "";
        }
    }

    private void PersistSkinSelection()
    {
        _settings.Settings.SkinPath = SkinPath;
        _settings.Settings.SkinModel = SkinModel;
        _settings.Settings.CapePath = CapePath;
        _settings.Save();
        _saved?.Invoke();
    }

    private void Save()
    {
        if (string.IsNullOrWhiteSpace(PlayerName))
        {
            _toast("玩家名称不能为空。", true);
            return;
        }

        _settings.Settings.PlayerName = PlayerName.Trim();
        _settings.Settings.SkinPath = SkinPath;
        _settings.Settings.SkinModel = SkinModel;
        _settings.Settings.CapePath = CapePath;
        _settings.Settings.ProfileUuid = _profileUuid;
        _settings.Save();
        OnPropertyChanged(nameof(UuidText));
        _saved?.Invoke();
        _toast("账户与皮肤已保存。", false);
    }

    private void ImportSkin()
    {
        var dialog = new OpenFileDialog
        {
            Title = "选择 64×64 皮肤文件",
            Filter = "Minecraft 皮肤 (*.png)|*.png",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        if (!_skins.Validate(dialog.FileName, out var error))
        {
            _toast(error, true);
            return;
        }

        try
        {
            var target = _skins.Import(dialog.FileName);
            SkinPath = target;
            SkinModel = SkinModel;
            LoadLibrary();
            _selectedSkin = Skins.FirstOrDefault(s => string.Equals(s.Path, target, StringComparison.OrdinalIgnoreCase));
            foreach (var entry in Skins)
            {
                entry.IsSelected = ReferenceEquals(entry, _selectedSkin);
            }

            OnPropertyChanged(nameof(SelectedSkin));
            PersistSkinSelection();
            _toast("皮肤已导入并应用。", false);
        }
        catch (Exception ex)
        {
            _toast($"导入皮肤失败：{ex.Message}", true);
        }
    }

    private void UseDefault()
    {
        SkinPath = _skins.DefaultSkinPath;
        SkinModel = "Steve";
        CapePath = _skins.FindCapeFor(SkinPath) ?? "";
        LoadLibrary();
        PersistSkinSelection();
        _toast("已切换到默认 Steve 皮肤。", false);
    }

    private void OpenFolder()
    {
        try
        {
            Process.Start(new ProcessStartInfo(_skins.SkinsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast($"无法打开皮肤文件夹：{ex.Message}", true);
        }
    }

    private async Task LookupAsync()
    {
        IsLookingUp = true;
        ProfileStatus = "正在向 Mojang 查询档案…";
        try
        {
            var profile = await _mojang.LookupAsync(ProfileLookup);
            if (profile == null)
            {
                ProfileStatus = "没有找到该玩家，或 Mojang API 暂时不可用。";
                return;
            }

            if (string.IsNullOrWhiteSpace(profile.SkinUrl))
            {
                ProfileStatus = $"找到了 {profile.Name}（{profile.Uuid}），但该档案没有公开皮肤。";
                return;
            }

            var skinBytes = await _mojang.DownloadAsync(profile.SkinUrl);
            if (skinBytes == null)
            {
                ProfileStatus = "找到了档案，但皮肤文件下载失败。";
                return;
            }

            var path = _skins.SaveDownloaded(skinBytes, profile.Name);
            if (!_skins.Validate(path, out var error))
            {
                ProfileStatus = error;
                return;
            }

            if (!string.IsNullOrWhiteSpace(profile.CapeUrl))
            {
                var capeBytes = await _mojang.DownloadAsync(profile.CapeUrl);
                if (capeBytes != null)
                {
                    CapePath = _skins.SaveCape(capeBytes, profile.Name);
                }
            }

            SkinPath = path;
            SkinModel = profile.SkinModel;
            _profileUuid = profile.Uuid;
            OnPropertyChanged(nameof(UuidText));
            LoadLibrary();
            _selectedSkin = Skins.FirstOrDefault(s => string.Equals(s.Path, path, StringComparison.OrdinalIgnoreCase));
            foreach (var entry in Skins)
            {
                entry.IsSelected = ReferenceEquals(entry, _selectedSkin);
            }

            OnPropertyChanged(nameof(SelectedSkin));
            PersistSkinSelection();
            ProfileStatus = $"已获取 {profile.Name} 的皮肤外观（UUID {profile.Uuid}）。这不会登录正版账号。";
            _toast($"已获取 {profile.Name} 的皮肤。", false);
        }
        catch (Exception ex)
        {
            ProfileStatus = $"查询失败：{ex.Message}";
        }
        finally
        {
            IsLookingUp = false;
        }
    }

    private bool TryGetProfileUuid(out string uuid)
    {
        uuid = "";
        var value = _settings.Settings.ProfileUuid;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        uuid = MojangProfileService.NormalizeUuid(value);
        return true;
    }

    private static string CreateOfflineUuid(string name)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + name));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
