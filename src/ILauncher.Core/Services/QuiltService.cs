using System.Text.Json;
using ILauncher.Core.Minecraft;

namespace ILauncher.Core.Services;

public sealed record QuiltLoaderVersion(string Version);

public sealed class QuiltService
{
    public async Task<List<QuiltLoaderVersion>> GetLoadersAsync(string minecraftVersion, CancellationToken cancellationToken = default)
    {
        var json = await DownloadHelper.GetStringAsync(
            $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(minecraftVersion)}", cancellationToken);
        using var document = JsonDocument.Parse(json);
        var result = new List<QuiltLoaderVersion>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (element.TryGetProperty("loader", out var loader) &&
                loader.TryGetProperty("version", out var version) &&
                version.GetString() is { Length: > 0 } value)
            {
                result.Add(new QuiltLoaderVersion(value));
            }
        }

        return result;
    }

    public async Task<string> InstallAsync(string minecraftVersion, string? loaderVersion, string rootDirectory,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        var baseJson = Path.Combine(rootDirectory, "versions", minecraftVersion, minecraftVersion + ".json");
        if (!File.Exists(baseJson))
        {
            throw new InvalidOperationException($"请先安装原版 {minecraftVersion}，Quilt 需要以它为基础。");
        }

        var loaders = await GetLoadersAsync(minecraftVersion, cancellationToken);
        var loader = !string.IsNullOrWhiteSpace(loaderVersion)
            ? loaders.FirstOrDefault(l => l.Version == loaderVersion)
            : loaders.FirstOrDefault();
        if (loader == null)
        {
            throw new InvalidOperationException("没有找到可用的 Quilt Loader 版本。");
        }

        progress?.Invoke(new DownloadProgress { Phase = "Quilt", Message = $"正在获取 Quilt {loader.Version} 配置…", Percent = 3 });
        var url = $"https://meta.quiltmc.org/v3/versions/loader/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(loader.Version)}/profile/json";
        var json = await DownloadHelper.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var id = document.RootElement.TryGetProperty("id", out var idElement)
            ? idElement.GetString()
            : $"quilt-loader-{loader.Version}-{minecraftVersion}";
        id = string.IsNullOrWhiteSpace(id) ? $"quilt-loader-{loader.Version}-{minecraftVersion}" : id;

        var directory = Path.Combine(rootDirectory, "versions", id);
        Directory.CreateDirectory(directory);
        var targetJson = Path.Combine(directory, id + ".json");
        await File.WriteAllTextAsync(targetJson, json, cancellationToken);

        var metadata = VersionMetadata.Parse(targetJson);
        var files = new List<(string Url, string Path, string? Sha1)>();
        foreach (var library in metadata.Libraries)
        {
            if (!Allowed(library.Rules) || library.Artifact?.Url is not { Length: > 0 } libraryUrl)
            {
                continue;
            }

            var path = library.RelativeArtifactPath;
            if (path != null)
            {
                files.Add((libraryUrl, Path.Combine(rootDirectory, "libraries", path.Replace('/', Path.DirectorySeparatorChar)),
                    library.Artifact.Sha1));
            }
        }

        var completed = 0;
        foreach (var file in files)
        {
            try
            {
                await DownloadHelper.DownloadFileAsync(file.Url, file.Path, null, cancellationToken, file.Sha1);
            }
            catch
            {
                // Continue; launch will report missing libraries.
            }

            completed++;
            progress?.Invoke(new DownloadProgress
            {
                Phase = "Quilt",
                Message = $"{completed}/{files.Count}",
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
