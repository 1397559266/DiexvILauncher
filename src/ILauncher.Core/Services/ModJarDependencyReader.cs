using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ILauncher.Core.Services;

public sealed record JarDependency(string ModId, bool Mandatory, string VersionRange);

/// <summary>
/// Reads declared mod dependencies from a mod jar. Modrinth versions sometimes
/// have an empty dependencies array even when the mod metadata declares several
/// prerequisites (notably Forge/NeoForge mods.toml).
/// </summary>
public static class ModJarDependencyReader
{
    private static readonly HashSet<string> BuiltInDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "forge", "neoforge", "fabricloader", "fabric-loader", "fabric",
        "quilt_loader", "quilt-loader", "java", "javafml", "lowcodefml", "neoforge"
    };

    public static IReadOnlyList<JarDependency> Read(string jarPath)
    {
        var result = new List<JarDependency>();
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            ReadModsToml(archive, result);
            ReadFabricJson(archive, result);
            ReadQuiltJson(archive, result);
        }
        catch
        {
            return Array.Empty<JarDependency>();
        }

        return result
            .Where(item => !BuiltInDependencies.Contains(item.ModId))
            .GroupBy(item => item.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Mandatory).First())
            .ToList();
    }

    private static void ReadModsToml(ZipArchive archive, List<JarDependency> result)
    {
        var entry = archive.Entries.FirstOrDefault(item =>
            item.FullName.Equals("META-INF/mods.toml", StringComparison.OrdinalIgnoreCase) ||
            item.FullName.Equals("META-INF/neoforge.mods.toml", StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            return;
        }

        using var reader = new StreamReader(entry.Open());
        var toml = reader.ReadToEnd();
        foreach (Match section in Regex.Matches(toml,
                     @"\[\[dependencies\.[^\]]+\]\](?<body>.*?)(?=\[\[|\z)",
                     RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            var body = section.Groups["body"].Value;
            var modId = MatchValue(body, "modId");
            if (string.IsNullOrWhiteSpace(modId))
            {
                continue;
            }

            var mandatory = !Regex.IsMatch(body, @"mandatory\s*=\s*false", RegexOptions.IgnoreCase);
            var versionRange = MatchValue(body, "versionRange") ?? "";
            result.Add(new JarDependency(modId, mandatory, versionRange));
        }
    }

    private static void ReadFabricJson(ZipArchive archive, List<JarDependency> result)
    {
        var entry = archive.Entries.FirstOrDefault(item =>
            item.FullName.Equals("fabric.mod.json", StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            return;
        }

        using var document = JsonDocument.Parse(entry.Open());
        if (document.RootElement.TryGetProperty("depends", out var depends) &&
            depends.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in depends.EnumerateObject())
            {
                result.Add(new JarDependency(property.Name, true, property.Value.ToString()));
            }
        }

        if (document.RootElement.TryGetProperty("recommends", out var recommends) &&
            recommends.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in recommends.EnumerateObject())
            {
                result.Add(new JarDependency(property.Name, false, property.Value.ToString()));
            }
        }
    }

    private static void ReadQuiltJson(ZipArchive archive, List<JarDependency> result)
    {
        var entry = archive.Entries.FirstOrDefault(item =>
            item.FullName.Equals("quilt.mod.json", StringComparison.OrdinalIgnoreCase));
        if (entry == null)
        {
            return;
        }

        using var document = JsonDocument.Parse(entry.Open());
        if (!document.RootElement.TryGetProperty("quilt_loader", out var loader) ||
            !loader.TryGetProperty("depends", out var depends) ||
            depends.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in depends.EnumerateObject())
        {
            result.Add(new JarDependency(property.Name, true, property.Value.ToString()));
        }
    }

    private static string? MatchValue(string body, string key)
    {
        var match = Regex.Match(body, key + @"\s*=\s*""(?<value>[^""]*)""", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }
}
