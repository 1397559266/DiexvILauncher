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
    public string MicrosoftAccessTokenProtected { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string MicrosoftAccessToken { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string MicrosoftRefreshToken { get; set; } = "";

    [System.Text.Json.Serialization.JsonIgnore]
    public string MicrosoftUuid { get; set; } = "";
    public string JavaPath { get; set; } = "";
    public int MemoryMb { get; set; } = 4096;
    public int HomeVersionsPerRow { get; set; } = 3;
    public string DownloadSource { get; set; } = "official";
    public int WindowWidth { get; set; } = 1280;
    public int WindowHeight { get; set; } = 720;
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
        if (!string.IsNullOrWhiteSpace(Settings.MicrosoftClientId))
        {
            return;
        }

        var clientId = Environment.GetEnvironmentVariable("MS_CLIENT_ID")
                       ?? Environment.GetEnvironmentVariable("ILAUNCHER_MS_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(clientId))
        {
            Settings.MicrosoftClientId = clientId.Trim();
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
}
