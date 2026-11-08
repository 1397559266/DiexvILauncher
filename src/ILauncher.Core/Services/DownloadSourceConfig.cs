namespace ILauncher.Core.Services;

/// <summary>
/// Selects between Mojang's official CDN and the BMCLAPI mirror for
/// Minecraft game files. Modrinth and loader metadata remain on their own
/// official endpoints.
/// </summary>
public static class DownloadSourceConfig
{
    public const string MirrorBase = "https://bmclapi2.bangbang93.com";

    public static bool PreferMirror { get; set; }

    public static string ManifestUrl => PreferMirror
        ? MirrorBase + "/mc/game/version_manifest_v2.json"
        : "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public static string VersionJson(string id, string officialUrl)
        => PreferMirror ? $"{MirrorBase}/version/{Uri.EscapeDataString(id)}/json" : officialUrl;

    public static string ClientJar(string id, string officialUrl)
        => PreferMirror ? $"{MirrorBase}/version/{Uri.EscapeDataString(id)}/client" : officialUrl;

    public static string Library(string officialUrl)
    {
        if (!PreferMirror)
        {
            return officialUrl;
        }

        return officialUrl.Replace("https://libraries.minecraft.net/", MirrorBase + "/maven/",
            StringComparison.OrdinalIgnoreCase);
    }

    public static string AssetObject(string hash)
        => PreferMirror
            ? $"{MirrorBase}/assets/{hash[..2]}/{hash}"
            : $"https://resources.download.minecraft.net/{hash[..2]}/{hash}";
}
