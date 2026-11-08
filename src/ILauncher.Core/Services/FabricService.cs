using System.Text.Json;
using ILauncher.Core.Minecraft;

namespace ILauncher.Core.Services;

public sealed record FabricLoaderVersion(string Version, bool Stable);

public sealed class FabricService
{
    public async Task<List<FabricLoaderVersion>> GetLoadersAsync(string minecraftVersion, CancellationToken cancellationToken = default)
    {
        var json = await DownloadHelper.GetStringAsync(
            $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}", cancellationToken);
        using var document = JsonDocument.Parse(json);
        var result = new List<FabricLoaderVersion>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (!element.TryGetProperty("loader", out var loader))
            {
                continue;
            }

            var version = loader.TryGetProperty("version", out var v) ? v.GetString() : null;
            if (string.IsNullOrWhiteSpace(version))
            {
                continue;
            }

            result.Add(new FabricLoaderVersion(version,
                loader.TryGetProperty("stable", out var stable) && stable.ValueKind == JsonValueKind.True));
        }

        return result;
    }

    public async Task<string> InstallAsync(string minecraftVersion, string? loaderVersion, string rootDirectory,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        var baseJson = Path.Combine(rootDirectory, "versions", minecraftVersion, minecraftVersion + ".json");
        if (!File.Exists(baseJson))
        {
            throw new InvalidOperationException($"请先安装原版 {minecraftVersion}，Fabric 需要以它为基础。");
        }

        var loaders = await GetLoadersAsync(minecraftVersion, cancellationToken);
        var loader = !string.IsNullOrWhiteSpace(loaderVersion)
            ? loaders.FirstOrDefault(l => l.Version == loaderVersion)
            : loaders.FirstOrDefault(l => l.Stable) ?? loaders.FirstOrDefault();
        if (loader == null)
        {
            throw new InvalidOperationException("没有找到可用的 Fabric Loader 版本。");
        }

        progress?.Invoke(new DownloadProgress { Phase = "Fabric", Message = $"正在获取 Fabric {loader.Version} 配置…", Percent = 3 });
        var url = $"https://meta.fabricmc.net/v2/versions/loader/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(loader.Version)}/profile/json";
        var json = await DownloadHelper.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var id = document.RootElement.TryGetProperty("id", out var idElement)
            ? idElement.GetString()
            : $"fabric-loader-{loader.Version}-{minecraftVersion}";
        id = string.IsNullOrWhiteSpace(id) ? $"fabric-loader-{loader.Version}-{minecraftVersion}" : id;

        var directory = Path.Combine(rootDirectory, "versions", id);
        Directory.CreateDirectory(directory);
        var targetJson = Path.Combine(directory, id + ".json");
        await File.WriteAllTextAsync(targetJson, json, cancellationToken);

        var metadata = VersionMetadata.Parse(targetJson);
        var files = new List<(string Url, string Path, string Name)>();
        foreach (var library in metadata.Libraries)
        {
            if (!Allowed(library.Rules) || library.Artifact?.Url is not { Length: > 0 } libraryUrl)
            {
                continue;
            }

            var path = library.RelativeArtifactPath;
            if (path == null)
            {
                continue;
            }

            files.Add((libraryUrl, Path.Combine(rootDirectory, "libraries", path.Replace('/', Path.DirectorySeparatorChar)),
                library.Name));
        }

        var completed = 0;
        foreach (var file in files)
        {
            try
            {
                await DownloadHelper.DownloadFileAsync(file.Url, file.Path, null, cancellationToken);
            }
            catch
            {
                // Continue; launch will report missing libraries.
            }

            completed++;
            progress?.Invoke(new DownloadProgress
            {
                Phase = "Fabric",
                Message = $"{completed}/{files.Count} · {Path.GetFileName(file.Path)}",
                Percent = files.Count == 0 ? 100 : completed * 100d / files.Count
            });
        }

        progress?.Invoke(new DownloadProgress { Phase = "完成", Message = $"已安装 {id}", Percent = 100 });
        return id;
    }

    private static bool Allowed(List<Rule> rules)
    {
        if (rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Matches(new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)))
            {
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }
}
