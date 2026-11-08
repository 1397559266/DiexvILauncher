using ILauncher.Core.Minecraft;
using ILauncher.Core.Models;

namespace ILauncher.Core.Services;

public sealed class InstallationService
{
    private readonly SettingsService _settings;

    public InstallationService(SettingsService settings)
    {
        _settings = settings;
    }

    public List<string> Warnings { get; } = new();

    public string? ResolveRoot()
    {
        var configured = _settings.Settings.MinecraftRoot;
        if (!string.IsNullOrWhiteSpace(configured) && Directory.Exists(configured))
        {
            return configured;
        }

        var candidates = new List<string>
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"),
            @"C:\Games\Miencraft\.minecraft",
            Path.Combine(AppContext.BaseDirectory, ".minecraft"),
            @"C:\MCmods\.minecraft"
        };

        var scored = candidates
            .Where(Directory.Exists)
            .Select(path => (Path: path, Score: Score(path)))
            .OrderByDescending(item => item.Score)
            .ToList();

        var best = scored.FirstOrDefault();
        if (best.Path == null || best.Score <= 0)
        {
            return Directory.Exists(candidates[0]) ? candidates[0] : null;
        }

        return best.Path;
    }

    private static int Score(string root)
    {
        try
        {
            var versions = Directory.Exists(Path.Combine(root, "versions"))
                ? Directory.EnumerateDirectories(Path.Combine(root, "versions")).Count(d => File.Exists(Path.Combine(d, Path.GetFileName(d) + ".json")))
                : 0;
            var assetIndexes = Directory.Exists(Path.Combine(root, "assets", "indexes"))
                ? Directory.EnumerateFiles(Path.Combine(root, "assets", "indexes")).Count()
                : 0;
            var hasLibraries = Directory.Exists(Path.Combine(root, "libraries"));
            return versions * 100 + assetIndexes * 5 + (hasLibraries ? 50 : 0);
        }
        catch
        {
            return 0;
        }
    }

    public async Task<List<GameInstance>> LoadInstancesAsync(string rootDirectory, CancellationToken cancellationToken = default)
    {
        var versionsDirectory = Path.Combine(rootDirectory, "versions");
        if (!Directory.Exists(versionsDirectory))
        {
            return new List<GameInstance>();
        }

        Warnings.Clear();
        var directories = Directory.EnumerateDirectories(versionsDirectory).ToList();
        var instances = new List<GameInstance>();

        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var id = Path.GetFileName(directory);
            var jsonPath = Path.Combine(directory, id + ".json");
            if (!File.Exists(jsonPath))
            {
                continue;
            }

            try
            {
                var instance = await Task.Run(() => ProbeInstance(rootDirectory, directory, id, jsonPath), cancellationToken);
                if (instance != null)
                {
                    instances.Add(instance);
                }
            }
            catch (Exception ex)
            {
                Warnings.Add($"{id}: {ex.Message}");
            }
        }

        return instances
            .OrderByDescending(i => i.LastWriteTime)
            .ThenBy(i => i.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static GameInstance? ProbeInstance(string root, string directory, string id, string jsonPath)
    {
        var metadata = VersionMetadata.Parse(jsonPath);
        var minecraftVersion = VersionResolver.DetectMinecraftVersion(id, metadata);
        var (loader, loaderVersion) = VersionResolver.DetectLoader(id, metadata);
        var javaMajor = VersionResolver.GuessJavaMajor(minecraftVersion, metadata);

        var (enabledMods, disabledMods) = CountMods(Path.Combine(directory, "mods"));
        var isolated = Directory.Exists(Path.Combine(directory, "mods"))
                       || Directory.Exists(Path.Combine(directory, "saves"))
                       || File.Exists(Path.Combine(directory, "options.txt"));

        var lastWrite = File.GetLastWriteTime(jsonPath);
        var jarPath = Path.Combine(directory, id + ".jar");
        if (File.Exists(jarPath))
        {
            lastWrite = Max(lastWrite, File.GetLastWriteTime(jarPath));
        }

        return new GameInstance
        {
            Id = id,
            Directory = directory,
            JsonPath = jsonPath,
            RootDirectory = root,
            MinecraftVersion = minecraftVersion,
            Loader = loader,
            LoaderVersion = loaderVersion,
            Type = metadata.Type,
            RequiredJavaMajor = javaMajor,
            IsIsolated = isolated,
            LastWriteTime = lastWrite,
            ModCount = enabledMods + disabledMods,
            EnabledModCount = enabledMods
        };
    }

    private static DateTime Max(DateTime a, DateTime b) => a > b ? a : b;

    private static (int Enabled, int Disabled) CountMods(string modsDirectory)
    {
        if (!Directory.Exists(modsDirectory))
        {
            return (0, 0);
        }

        var enabled = 0;
        var disabled = 0;
        foreach (var file in Directory.EnumerateFiles(modsDirectory))
        {
            if (file.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
            {
                enabled++;
            }
            else if (file.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase))
            {
                disabled++;
            }
        }

        return (enabled, disabled);
    }
}
