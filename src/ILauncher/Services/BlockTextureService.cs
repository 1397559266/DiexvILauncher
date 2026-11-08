using System.IO;
using System.IO.Compression;
using System.Text.Json;
using ILauncher.Core.Services;

namespace ILauncher.Services;

/// <summary>
/// Extracts the official block/item textures used by the version list from the
/// user's own client jar. No Mojang texture is bundled in the repository.
/// </summary>
public static class BlockTextureService
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, string?> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static string? GetTexturePath(string key)
    {
        lock (Gate)
        {
            if (Cache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var target = Path.Combine(AppPaths.DataDirectory, "textures", key + ".png");
            if (File.Exists(target))
            {
                Cache[key] = target;
                return target;
            }

            var extracted = Extract(key, target);
            Cache[key] = extracted;
            return extracted;
        }
    }

    private static string? Extract(string key, string target)
    {
        var candidates = key switch
        {
            "grass" => new[]
            {
                "assets/minecraft/textures/block/grass_block_side.png",
                "assets/minecraft/textures/blocks/grass_side.png",
                "assets/minecraft/textures/block/grass_block_top.png",
                "assets/minecraft/textures/blocks/grass_top.png"
            },
            "command" => new[]
            {
                "assets/minecraft/textures/block/command_block_front.png",
                "assets/minecraft/textures/block/command_block.png",
                "assets/minecraft/textures/blocks/command_block.png"
            },
            "potato" => new[]
            {
                "assets/minecraft/textures/item/potato.png",
                "assets/minecraft/textures/items/potato.png"
            },
            _ => Array.Empty<string>()
        };
        if (candidates.Length == 0)
        {
            return null;
        }

        foreach (var root in CandidateMinecraftRoots())
        {
            var versions = Path.Combine(root, "versions");
            if (!Directory.Exists(versions))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(versions))
            {
                var versionName = Path.GetFileName(versionDirectory);
                var preferred = Path.Combine(versionDirectory, versionName + ".jar");
                var jars = new List<string>();
                if (File.Exists(preferred))
                {
                    jars.Add(preferred);
                }

                jars.AddRange(Directory.EnumerateFiles(versionDirectory, "*.jar")
                    .Where(path => !string.Equals(path, preferred, StringComparison.OrdinalIgnoreCase)));

                foreach (var jar in jars)
                {
                    try
                    {
                        using var archive = ZipFile.OpenRead(jar);
                        foreach (var candidate in candidates)
                        {
                            var entry = archive.Entries.FirstOrDefault(item =>
                                string.Equals(item.FullName, candidate, StringComparison.OrdinalIgnoreCase));
                            if (entry == null || entry.Length == 0)
                            {
                                continue;
                            }

                            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                            using var source = entry.Open();
                            using var file = File.Create(target);
                            source.CopyTo(file);
                            return target;
                        }
                    }
                    catch
                    {
                        // Try the next jar.
                    }
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<string> CandidateMinecraftRoots()
    {
        var roots = new List<string>();
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
                if (document.RootElement.TryGetProperty("MinecraftRoot", out var root) &&
                    root.ValueKind == JsonValueKind.String)
                {
                    var path = root.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        roots.Add(path);
                    }
                }
            }
        }
        catch
        {
            // Use the standard locations below.
        }

        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft"));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}
