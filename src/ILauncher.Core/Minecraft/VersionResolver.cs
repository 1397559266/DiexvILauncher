using System.Text.RegularExpressions;
using ILauncher.Core.Models;

namespace ILauncher.Core.Minecraft;

public static partial class VersionResolver
{
    public static string GetVersionJsonPath(string root, string id)
        => Path.Combine(root, "versions", id, id + ".json");

    public static VersionMetadata Load(string rootDirectory, string versionId)
        => LoadInternal(rootDirectory, versionId, 0);

    private static VersionMetadata LoadInternal(string rootDirectory, string versionId, int depth)
    {
        if (depth > 8)
        {
            throw new InvalidOperationException($"版本继承层级过深：{versionId}");
        }

        var path = GetVersionJsonPath(rootDirectory, versionId);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"找不到版本文件：{path}", path);
        }

        var child = VersionMetadata.Parse(path);
        if (string.IsNullOrWhiteSpace(child.InheritsFrom))
        {
            return child;
        }

        var parent = LoadInternal(rootDirectory, child.InheritsFrom, depth + 1);
        return Merge(parent, child);
    }

    private static VersionMetadata Merge(VersionMetadata parent, VersionMetadata child)
    {
        var merged = parent.Clone();

        merged.Id = child.Id;
        merged.Type = child.Type;
        merged.InheritsFrom = child.InheritsFrom;
        merged.MainClass = string.IsNullOrWhiteSpace(child.MainClass) ? parent.MainClass : child.MainClass;
        merged.Assets = child.Assets ?? parent.Assets;
        merged.AssetIndexId = child.AssetIndexId ?? parent.AssetIndexId;
        merged.AssetIndexUrl = child.AssetIndexUrl ?? parent.AssetIndexUrl;
        merged.JavaMajor = child.JavaMajor > 0 ? child.JavaMajor : parent.JavaMajor;
        merged.LegacyArguments = child.LegacyArguments ?? parent.LegacyArguments;
        merged.ClientVersion = child.ClientVersion ?? parent.ClientVersion;
        merged.Client = child.Client ?? parent.Client;
        merged.Logging = child.Logging ?? parent.Logging;

        // Child arguments are appended after parent arguments.
        merged.JvmArguments.Clear();
        merged.JvmArguments.AddRange(parent.JvmArguments);
        merged.JvmArguments.AddRange(child.JvmArguments);
        merged.GameArguments.Clear();
        merged.GameArguments.AddRange(parent.GameArguments);
        merged.GameArguments.AddRange(child.GameArguments);

        // Child libraries override same-named parent entries.
        var libraries = new List<Library>();
        var indexByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var library in parent.Libraries.Concat(child.Libraries))
        {
            if (indexByName.TryGetValue(library.Name, out var index))
            {
                libraries[index] = library;
            }
            else
            {
                indexByName[library.Name] = libraries.Count;
                libraries.Add(library);
            }
        }

        merged.Libraries.Clear();
        merged.Libraries.AddRange(libraries);
        return merged;
    }

    public static string? ResolveClientJar(GameInstance instance, VersionMetadata metadata)
    {
        var candidates = new List<string>();

        void AddFor(string? versionId)
        {
            if (string.IsNullOrWhiteSpace(versionId))
            {
                return;
            }

            candidates.Add(Path.Combine(instance.RootDirectory, "versions", versionId, versionId + ".jar"));
        }

        AddFor(instance.Id);
        if (metadata.Client?.Path is { Length: > 0 } path)
        {
            var fileName = Path.GetFileName(path);
            if (!string.IsNullOrWhiteSpace(fileName))
            {
                candidates.Add(Path.Combine(instance.Directory, fileName));
            }
        }

        // Walk up the inheritance chain.
        var current = metadata;
        var guard = 0;
        while (!string.IsNullOrWhiteSpace(current.InheritsFrom) && guard++ < 8)
        {
            AddFor(current.InheritsFrom);
            try
            {
                current = VersionMetadata.Parse(GetVersionJsonPath(instance.RootDirectory, current.InheritsFrom));
            }
            catch
            {
                break;
            }
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    public static string DetectMinecraftVersion(string id, VersionMetadata metadata)
    {
        var match = MinecraftVersionRegex().Match(id);
        if (match.Success)
        {
            return match.Value;
        }

        if (!string.IsNullOrWhiteSpace(metadata.ClientVersion))
        {
            match = MinecraftVersionRegex().Match(metadata.ClientVersion);
            if (match.Success)
            {
                return match.Value;
            }
        }

        if (!string.IsNullOrWhiteSpace(metadata.InheritsFrom))
        {
            match = MinecraftVersionRegex().Match(metadata.InheritsFrom);
            if (match.Success)
            {
                return match.Value;
            }
        }

        return "未知";
    }

    public static (string Loader, string LoaderVersion) DetectLoader(string id, VersionMetadata metadata)
    {
        var names = metadata.Libraries.Select(l => l.Name).ToList();
        var lowerId = id.ToLowerInvariant();

        bool Has(string token) => names.Any(n => n.Contains(token, StringComparison.OrdinalIgnoreCase));

        if (Has("net.neoforged") || lowerId.Contains("neoforge"))
        {
            return ("NeoForge", ExtractVersion(names, "net.neoforged") ?? ExtractTokenVersion(id, "neoforge"));
        }

        if (Has("net.fabricmc:fabric-loader") || lowerId.Contains("fabric"))
        {
            return ("Fabric", ExtractVersion(names, "net.fabricmc:fabric-loader") ?? ExtractTokenVersion(id, "fabric"));
        }

        if (Has("org.quiltmc") || lowerId.Contains("quilt"))
        {
            return ("Quilt", ExtractVersion(names, "org.quiltmc") ?? "");
        }

        if (Has("net.minecraftforge:forge") || Has("net.minecraftforge:fmlloader") || lowerId.Contains("forge"))
        {
            return ("Forge", ExtractVersion(names, "net.minecraftforge:forge")
                             ?? ExtractVersion(names, "net.minecraftforge:fmlloader")
                             ?? ExtractTokenVersion(id, "forge"));
        }

        if (Has("optifine") || lowerId.Contains("optifine"))
        {
            return ("OptiFine", "");
        }

        return ("原版", "");
    }

    public static int GuessJavaMajor(string minecraftVersion, VersionMetadata metadata)
    {
        if (metadata.JavaMajor > 0)
        {
            return metadata.JavaMajor;
        }

        if (TryParseMcVersion(minecraftVersion, out var major, out var minor, out var patch))
        {
            if (major > 1 || (major == 1 && minor >= 20 && patch >= 5))
            {
                return 21;
            }

            if (major == 1 && minor >= 18)
            {
                return 17;
            }

            if (major == 1 && minor >= 17)
            {
                return 16;
            }
        }

        return 8;
    }

    private static bool TryParseMcVersion(string version, out int major, out int minor, out int patch)
    {
        major = minor = patch = 0;
        var match = new Regex(@"^(\d+)\.(\d+)(?:\.(\d+))?").Match(version);
        if (!match.Success)
        {
            return false;
        }

        major = int.Parse(match.Groups[1].Value);
        minor = int.Parse(match.Groups[2].Value);
        patch = match.Groups[3].Success ? int.Parse(match.Groups[3].Value) : 0;
        return true;
    }

    private static string? ExtractVersion(IEnumerable<string> names, string groupAndArtifact)
    {
        foreach (var name in names)
        {
            var parts = name.Split(':');
            if (parts.Length < 3)
            {
                continue;
            }

            var coordinate = parts[0] + ":" + parts[1];
            if (string.Equals(coordinate, groupAndArtifact, StringComparison.OrdinalIgnoreCase))
            {
                return parts[2];
            }
        }

        return null;
    }

    private static string ExtractTokenVersion(string id, string token)
    {
        var match = Regex.Match(id, Regex.Escape(token) + @"[-_ ]?([\d][\w.\-]*)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups[1].Value.TrimEnd('-', '_') : "";
    }

    [GeneratedRegex(@"\d+\.\d+(?:\.\d+)?")]
    private static partial Regex MinecraftVersionRegex();
}
