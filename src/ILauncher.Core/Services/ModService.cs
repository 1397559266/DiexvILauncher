using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ILauncher.Core.Models;

namespace ILauncher.Core.Services;

public sealed partial class ModService
{
    public async Task<List<ModInfo>> LoadModsAsync(GameInstance instance, CancellationToken cancellationToken = default)
    {
        var modsDirectory = instance.ModsDirectory;
        if (!Directory.Exists(modsDirectory))
        {
            return new List<ModInfo>();
        }

        var files = Directory.EnumerateFiles(modsDirectory)
            .Where(path => path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                           || path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var result = new List<ModInfo>(files.Count);
        var throttle = new SemaphoreSlim(8);
        var tasks = files.Select(async path =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                var info = await Task.Run(() => ReadMod(path), cancellationToken);
                info.Name = StripFormatting(info.Name);
                info.Description = StripFormatting(info.Description);
                lock (result)
                {
                    result.Add(info);
                }
            }
            finally
            {
                throttle.Release();
            }
        });

        await Task.WhenAll(tasks);
        throttle.Dispose();

        return result
            .OrderByDescending(m => m.Enabled)
            .ThenBy(m => m.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static ModInfo ReadMod(string path)
    {
        var fileName = Path.GetFileName(path);
        var enabled = !fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var cleanName = fileName.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase)
            ? fileName[..^".jar.disabled".Length]
            : fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
                ? fileName[..^".jar".Length]
                : Path.GetFileNameWithoutExtension(fileName);

        var info = new ModInfo
        {
            FilePath = path,
            FileName = fileName,
            Enabled = enabled,
            Size = new FileInfo(path).Length,
            LastWriteTime = File.GetLastWriteTime(path),
            Name = CleanFileName(cleanName)
        };

        try
        {
            using var archive = ZipFile.OpenRead(path);

            var fabric = FindEntry(archive, "fabric.mod.json");
            if (fabric != null)
            {
                info.Loader = "Fabric";
                using var stream = fabric.Open();
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                info.Name = GetString(root, "name") ?? info.Name;
                info.Version = GetString(root, "version") ?? "";
                info.ModId = GetString(root, "id") ?? "";
                info.Description = GetString(root, "description") ?? "";
                var iconPath = GetString(root, "icon");
                if (!string.IsNullOrWhiteSpace(iconPath))
                {
                    info.IconData = TryReadIcon(archive, iconPath!);
                }

                return info;
            }

            var quilt = FindEntry(archive, "quilt.mod.json");
            if (quilt != null)
            {
                info.Loader = "Quilt";
                using var stream = quilt.Open();
                using var document = JsonDocument.Parse(stream);
                var root = document.RootElement;
                if (root.TryGetProperty("quilt_loader", out var loader))
                {
                    info.ModId = GetString(loader, "id") ?? "";
                    info.Version = GetString(loader, "version") ?? "";
                    if (loader.TryGetProperty("metadata", out var metadata))
                    {
                        info.Name = GetString(metadata, "name") ?? info.Name;
                        info.Description = GetString(metadata, "description") ?? "";
                        var icon = GetString(metadata, "icon");
                        if (!string.IsNullOrWhiteSpace(icon))
                        {
                            info.IconData = TryReadIcon(archive, icon!);
                        }
                    }
                }

                return info;
            }

            var neoforgeToml = FindEntry(archive, "META-INF/neoforge.mods.toml");
            var forgeToml = FindEntry(archive, "META-INF/mods.toml");
            var toml = neoforgeToml ?? forgeToml;
            if (toml != null)
            {
                info.Loader = neoforgeToml != null ? "NeoForge" : "Forge";
                using var stream = toml.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                var block = FirstModBlock(text);
                if (block != null)
                {
                    info.ModId = TomlValue(block, "modId") ?? "";
                    info.Name = TomlValue(block, "displayName") ?? info.Name;
                    info.Version = TomlValue(block, "version") ?? "";
                    info.Description = TomlValue(block, "description") ?? "";
                    var logo = TomlValue(block, "logoFile");
                    if (!string.IsNullOrWhiteSpace(logo))
                    {
                        info.IconData = TryReadIcon(archive, logo!);
                    }
                }

                if (string.IsNullOrWhiteSpace(info.Version))
                {
                    info.Version = TomlValue(text, "version") ?? "";
                }

                return info;
            }

            var manifest = FindEntry(archive, "META-INF/MANIFEST.MF");
            if (manifest != null)
            {
                using var stream = manifest.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                var text = reader.ReadToEnd();
                var title = ManifestValue(text, "Implementation-Title");
                var version = ManifestValue(text, "Implementation-Version");
                if (!string.IsNullOrWhiteSpace(title))
                {
                    info.Name = title!;
                }

                if (!string.IsNullOrWhiteSpace(version))
                {
                    info.Version = version!;
                }

                var modType = ManifestValue(text, "FMLModType");
                if (!string.IsNullOrWhiteSpace(modType))
                {
                    info.Loader = "Forge";
                }
            }
        }
        catch
        {
            // A broken jar should still be listed so the user can disable it.
        }

        if (string.IsNullOrWhiteSpace(info.Version))
        {
            info.Version = GuessVersion(cleanName);
        }

        return info;
    }

    public Task ToggleAsync(ModInfo mod, bool enable)
    {
        return Task.Run(() =>
        {
            var path = mod.FilePath;
            string target;
            if (enable)
            {
                if (!path.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                target = path[..^".disabled".Length];
            }
            else
            {
                if (!path.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                target = path + ".disabled";
            }

            if (File.Exists(target))
            {
                throw new IOException($"目标文件已存在：{Path.GetFileName(target)}");
            }

            File.Move(path, target);
            mod.FilePath = target;
            mod.FileName = Path.GetFileName(target);
            mod.Enabled = enable;
        });
    }

    public async Task<int> InstallAsync(IEnumerable<string> sourceFiles, string modsDirectory)
    {
        Directory.CreateDirectory(modsDirectory);
        var copied = 0;
        foreach (var source in sourceFiles)
        {
            if (!File.Exists(source) ||
                !source.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var fileName = Path.GetFileName(source);
            var target = Path.Combine(modsDirectory, fileName);
            if (File.Exists(target))
            {
                var baseName = Path.GetFileNameWithoutExtension(fileName);
                var index = 2;
                do
                {
                    target = Path.Combine(modsDirectory, $"{baseName} ({index++}).jar");
                } while (File.Exists(target));
            }

            await Task.Run(() => File.Copy(source, target));
            copied++;
        }

        return copied;
    }

    private static ZipArchiveEntry? FindEntry(ZipArchive archive, string name)
        => archive.Entries.FirstOrDefault(e =>
            string.Equals(e.FullName.Replace('\\', '/'), name, StringComparison.OrdinalIgnoreCase));

    private static byte[]? TryReadIcon(ZipArchive archive, string iconPath)
    {
        try
        {
            var normalized = iconPath.Replace('\\', '/').TrimStart('/');
            var entry = FindEntry(archive, normalized);
            if (entry == null || entry.Length == 0 || entry.Length > 2 * 1024 * 1024)
            {
                return null;
            }

            using var stream = entry.Open();
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        }
        catch
        {
            return null;
        }
    }

    private static string? FirstModBlock(string toml)
    {
        var match = Regex.Match(toml, @"\[\[mods\]\](?<body>.*?)(?=\[\[|\z)", RegexOptions.Singleline);
        return match.Success ? match.Groups["body"].Value : toml;
    }

    private static string? TomlValue(string text, string key)
    {
        var match = Regex.Match(text, $@"^\s*{Regex.Escape(key)}\s*=\s*(?:""(?<v>(?:[^""\\]|\\.)*)""|'(?<v>[^']*)')",
            RegexOptions.Multiline);
        if (!match.Success)
        {
            return null;
        }

        return Regex.Unescape(match.Groups["v"].Value).Replace("\\n", " ").Trim();
    }

    private static string? ManifestValue(string text, string key)
    {
        foreach (var line in text.Split('\n'))
        {
            var index = line.IndexOf(':');
            if (index <= 0)
            {
                continue;
            }

            if (string.Equals(line[..index].Trim(), key, StringComparison.OrdinalIgnoreCase))
            {
                return line[(index + 1)..].Trim();
            }
        }

        return null;
    }

    private static string CleanFileName(string name)
    {
        var cleaned = Regex.Replace(name, @"[-_ ]v?\d[\w.\-+ ]*$", "").Trim(' ', '-', '_');
        return string.IsNullOrWhiteSpace(cleaned) ? name : cleaned;
    }

    private static string StripFormatting(string value)
        => string.IsNullOrEmpty(value) ? value : Regex.Replace(value, @"[§$][0-9a-fk-orx]", "", RegexOptions.IgnoreCase);

    private static string GuessVersion(string name)
    {
        var match = Regex.Match(name, @"(\d+(?:\.\d+)+(?:[-+][\w.\-]+)?)");
        return match.Success ? match.Groups[1].Value : "";
    }

    private static string? GetString(JsonElement element, string property)
    {
        if (!element.TryGetProperty(property, out var value))
        {
            return null;
        }

        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Object when value.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String
                => path.GetString(),
            _ => null
        };
    }
}
