namespace ILauncher.Core.Models;

/// <summary>A Minecraft version/instance discovered in a .minecraft installation.</summary>
public sealed class GameInstance
{
    public required string Id { get; init; }

    /// <summary>Absolute path of <c>versions\&lt;id&gt;</c>.</summary>
    public required string Directory { get; init; }

    public required string JsonPath { get; init; }

    public required string RootDirectory { get; init; }

    public string MinecraftVersion { get; init; } = "未知";

    public string Loader { get; init; } = "原版";

    public string LoaderVersion { get; init; } = "";

    public string Type { get; init; } = "release";

    public int RequiredJavaMajor { get; init; } = 8;

    public bool IsIsolated { get; init; }

    public DateTime LastWriteTime { get; init; }

    public int ModCount { get; set; }

    public int EnabledModCount { get; set; }

    public string ModsDirectory => Path.Combine(Directory, "mods");

    public string GameDirectory => IsIsolated ? Directory : RootDirectory;

    public string JavaLabel => $"Java {RequiredJavaMajor}+";

    public string TypeLabel => Type switch
    {
        "release" => "正式版",
        "snapshot" => "快照",
        "old_beta" => "远古测试版",
        "old_alpha" => "远古版",
        _ => Type
    };

    public string Subtitle
    {
        get
        {
            var parts = new List<string> { MinecraftVersion };
            if (!string.Equals(Loader, "原版", StringComparison.Ordinal))
            {
                parts.Add(string.IsNullOrWhiteSpace(LoaderVersion) ? Loader : $"{Loader} {LoaderVersion}");
            }

            return string.Join(" · ", parts);
        }
    }
}
