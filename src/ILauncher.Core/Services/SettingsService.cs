using System.Text.Json;
using ILauncher.Core.Models;

namespace ILauncher.Core.Services;

public sealed class LauncherSettings
{
    public string MinecraftRoot { get; set; } = "";
    public string PlayerName { get; set; } = Environment.UserName;
    public string SkinPath { get; set; } = "";
    public string SkinModel { get; set; } = "Steve";
    public string CapePath { get; set; } = "";
    public string ProfileUuid { get; set; } = "";
    public string AccountType { get; set; } = "离线账户";
    public string MicrosoftClientId { get; set; } = "";

    /// <summary>browser：授权码 + PKCE 回环回调；device：设备码。</summary>
    public string MicrosoftAuthMode { get; set; } = "browser";

    /// <summary>回环回调端口，必须与 Azure 应用注册中的重定向 URI 一致。</summary>
    public int MicrosoftRedirectPort { get; set; } = MicrosoftAuthService.DefaultRedirectPort;

    /// <summary>上次登录成功时记住的微软账户名，用于 login_hint 免选账号。</summary>
    public string MicrosoftLoginHint { get; set; } = "";

    /// <summary>最近一次正版登录是否通过了游戏所有权（entitlements）校验。</summary>
    public bool MicrosoftOwnsGame { get; set; }

    /// <summary>是否已经向用户说明过“离线账户只能本地单人/离线服务器”。</summary>
    public bool OfflineLaunchAcknowledged { get; set; }

    public string MicrosoftAccessTokenProtected { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string MicrosoftAccessToken { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string MicrosoftRefreshToken { get; set; } = "";

    public string MicrosoftRefreshTokenProtected { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string MicrosoftUuid { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public int HomeVersionsPerRow { get; set; } = 3;
    public string DownloadSource { get; set; } = "official";
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
    public string LaunchWindowMode { get; set; } = "default";
    public string UiLanguage { get; set; } = "zh-CN";
    public string LastInstance { get; set; } = "";
    public bool AutoDetectJava { get; set; } = true;
    public bool CloseLauncherOnGameStart { get; set; }
    public Dictionary<string, InstanceOverrides> Instances { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class InstanceOverrides
{
    public string JavaPath { get; set; } = "";
    public int? MemoryMb { get; set; }
    public int? Width { get; set; }
    public int? Height { get; set; }
}

public sealed class SettingsService
{
    // Public Azure client ID. This is not a secret; it is the application ID
    // submitted for the Minecraft API review. MS_CLIENT_ID can override it.
    public const string BundledMicrosoftClientId = "24255f36-74b9-48be-83b0-6b738d829b39";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    public LauncherSettings Settings { get; private set; } = new();

    public SettingsService()
    {
        AppPaths.EnsureCreated();
        Load();
        ApplyClientIdFromEnvironment();
        DownloadSourceConfig.PreferMirror = string.Equals(Settings.DownloadSource, "mirror",
            StringComparison.OrdinalIgnoreCase);
    }

    private void ApplyClientIdFromEnvironment()
    {
        var clientId = Environment.GetEnvironmentVariable("MS_CLIENT_ID")
                       ?? Environment.GetEnvironmentVariable("ILAUNCHER_MS_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            Settings.MicrosoftClientId = clientId.Trim();
        }
        else if (string.IsNullOrWhiteSpace(Settings.MicrosoftClientId))
        {
            Settings.MicrosoftClientId = BundledMicrosoftClientId;
        }
    }

    public void Load()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile))
            {
                return;
            }

            var json = File.ReadAllText(AppPaths.SettingsFile);
            var settings = JsonSerializer.Deserialize<LauncherSettings>(json, JsonOptions);
            if (settings != null)
            {
                Settings = settings;
            }
        }
        catch
        {
            Settings = new LauncherSettings();
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                DownloadSourceConfig.PreferMirror = string.Equals(Settings.DownloadSource, "mirror",
                    StringComparison.OrdinalIgnoreCase);
                AppPaths.EnsureCreated();
                var json = JsonSerializer.Serialize(Settings, JsonOptions);
                var temp = AppPaths.SettingsFile + ".tmp";
                File.WriteAllText(temp, json);
                File.Move(temp, AppPaths.SettingsFile, overwrite: true);
            }
            catch
            {
                // Settings persistence must never crash the launcher.
            }
        }
    }

    public void ApplyLaunchDefaults(LaunchRequest request) => _ = request;

    public InstanceOverrides GetInstanceSettings(string instanceId)
    {
        if (Settings.Instances.TryGetValue(instanceId, out var value))
        {
            return value;
        }

        return new InstanceOverrides();
    }

    public void SaveInstanceSettings(string instanceId, InstanceOverrides overrides)
    {
        Settings.Instances[instanceId] = overrides;
        Save();
    }

    public string ResolvePlayerName()
        => string.IsNullOrWhiteSpace(Settings.PlayerName) ? Environment.UserName : Settings.PlayerName.Trim();

    public (int Width, int Height) ResolveWindowSize(string instanceId, double? launcherWidth = null,
        double? launcherHeight = null)
    {
        var overrides = GetInstanceSettings(instanceId);
        var mode = Settings.LaunchWindowMode;
        int width;
        int height;
        if (string.Equals(mode, "launcher", StringComparison.OrdinalIgnoreCase) &&
            launcherWidth is > 0 && launcherHeight is > 0)
        {
            width = (int)Math.Round(launcherWidth.Value);
            height = (int)Math.Round(launcherHeight.Value - 32);
        }
        else if (string.Equals(mode, "default", StringComparison.OrdinalIgnoreCase))
        {
            width = 854;
            height = 480;
        }
        else
        {
            width = Settings.WindowWidth;
            height = Settings.WindowHeight;
        }

        return (Math.Clamp(overrides.Width ?? width, 320, 7680),
                Math.Clamp(overrides.Height ?? height, 240, 4320));
    }
}
