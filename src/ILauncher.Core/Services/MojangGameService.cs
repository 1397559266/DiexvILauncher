using System.Text.Json;
using ILauncher.Core.Minecraft;

namespace ILauncher.Core.Services;

public sealed record RemoteVersion(
    string Id,
    string Type,
    string Url,
    DateTime ReleaseTime,
    string? Sha1)
{
    public static bool UseEnglish { get; set; }

    public string TypeText => Type switch
    {
        "release" => UseEnglish ? "Release" : "正式版",
        "snapshot" => UseEnglish ? "Snapshot" : "快照",
        "old_beta" => UseEnglish ? "Old Beta" : "远古测试版",
        "old_alpha" => UseEnglish ? "Old Alpha" : "远古版",
        _ => Type
    };
}

public sealed class MojangGameService
{
    private const string OfficialManifestUrl = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

    public async Task<List<RemoteVersion>> GetVersionsAsync(CancellationToken cancellationToken = default)
    {
        string json;
        try
        {
            json = await DownloadHelper.GetStringAsync(DownloadSourceConfig.ManifestUrl, cancellationToken);
        }
        catch when (DownloadSourceConfig.PreferMirror)
        {
            json = await DownloadHelper.GetStringAsync(OfficialManifestUrl, cancellationToken);
        }
        using var document = JsonDocument.Parse(json);
        var result = new List<RemoteVersion>();
        if (!document.RootElement.TryGetProperty("versions", out var versions))
        {
            return result;
        }

        foreach (var element in versions.EnumerateArray())
        {
            var id = GetString(element, "id");
            var url = GetString(element, "url");
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(url))
            {
                continue;
            }

            result.Add(new RemoteVersion(
                id,
                GetString(element, "type") ?? "release",
                url,
                DateTime.TryParse(GetString(element, "releaseTime"), out var time) ? time : DateTime.MinValue,
                GetString(element, "sha1")));
        }

        return result;
    }

    public async Task InstallAsync(RemoteVersion version, string rootDirectory, bool downloadAssets,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new DownloadProgress { Phase = "准备", Message = $"准备安装 {version.Id}…", Percent = 0 });
        var versionDirectory = Path.Combine(rootDirectory, "versions", version.Id);
        Directory.CreateDirectory(versionDirectory);
        var versionJsonPath = Path.Combine(versionDirectory, version.Id + ".json");

        await DownloadFileWithFallbackAsync(DownloadSourceConfig.VersionJson(version.Id, version.Url), version.Url,
            versionJsonPath, cancellationToken);
        var metadata = VersionMetadata.Parse(versionJsonPath);
        var clientJarPath = Path.Combine(versionDirectory, version.Id + ".jar");
        if (metadata.Client?.Url is { Length: > 0 } clientUrl)
        {
            progress?.Invoke(new DownloadProgress { Phase = "客户端", Message = "下载客户端 jar…", Percent = 2 });
            await DownloadFileWithFallbackAsync(DownloadSourceConfig.ClientJar(version.Id, clientUrl), clientUrl,
                clientJarPath, cancellationToken);
        }

        var files = new List<(string Url, string Path, string Name)>();
        foreach (var library in metadata.Libraries)
        {
            if (!Allowed(library.Rules))
            {
                continue;
            }

            var path = library.RelativeArtifactPath;
            if (path == null || library.Artifact?.Url is not { Length: > 0 } url)
            {
                continue;
            }

            files.Add((DownloadSourceConfig.Library(url), Path.Combine(rootDirectory, "libraries", path.Replace('/', Path.DirectorySeparatorChar)),
                library.Name));
        }

        var assetsRoot = Path.Combine(rootDirectory, "assets");
        var assetIndexId = string.IsNullOrWhiteSpace(metadata.AssetIndexId) ? metadata.Assets : metadata.AssetIndexId;
        if (!string.IsNullOrWhiteSpace(assetIndexId))
        {
            var indexUrl = metadata.AssetIndexUrl;
            var indexPath = Path.Combine(assetsRoot, "indexes", assetIndexId + ".json");
            if (!string.IsNullOrWhiteSpace(indexUrl))
            {
                await DownloadHelper.DownloadFileAsync(indexUrl, indexPath, null, cancellationToken);
            }

            if (downloadAssets && File.Exists(indexPath))
            {
                try
                {
                    using var document = JsonDocument.Parse(await File.ReadAllTextAsync(indexPath, cancellationToken));
                    if (document.RootElement.TryGetProperty("objects", out var objects))
                    {
                        foreach (var property in objects.EnumerateObject())
                        {
                            var hash = GetString(property.Value, "hash");
                            if (string.IsNullOrWhiteSpace(hash) || hash.Length < 2)
                            {
                                continue;
                            }

                            var target = Path.Combine(assetsRoot, "objects", hash[..2], hash);
                            files.Add((DownloadSourceConfig.AssetObject(hash), target, property.Name));
                        }
                    }
                }
                catch
                {
                    // If the index is malformed the game can still be installed; assets are optional.
                }
            }
        }

        if (metadata.Logging is { FileId: { Length: > 0 } logId, FileUrl: { Length: > 0 } logUrl })
        {
            files.Add((logUrl, Path.Combine(assetsRoot, "log_configs", logId), logId));
        }

        await DownloadFilesAsync(files, progress, cancellationToken);
        progress?.Invoke(new DownloadProgress { Phase = "完成", Message = $"已安装 {version.Id}", Percent = 100 });
    }

    private static async Task DownloadFileWithFallbackAsync(string primaryUrl, string fallbackUrl,
        string destination, CancellationToken cancellationToken)
    {
        try
        {
            await DownloadHelper.DownloadFileAsync(primaryUrl, destination, null, cancellationToken);
        }
        catch when (!string.Equals(primaryUrl, fallbackUrl, StringComparison.OrdinalIgnoreCase))
        {
            await DownloadHelper.DownloadFileAsync(fallbackUrl, destination, null, cancellationToken);
        }
    }

    private static async Task DownloadFilesAsync(List<(string Url, string Path, string Name)> files,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var pending = files
            .GroupBy(file => file.Path, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .ToList();
        var completed = 0;
        var total = pending.Count;
        var throttle = new SemaphoreSlim(8);
        var tasks = pending.Select(async file =>
        {
            await throttle.WaitAsync(cancellationToken);
            try
            {
                await DownloadHelper.DownloadFileAsync(file.Url, file.Path, null, cancellationToken);
            }
            catch (Exception)
            {
                // A single missing library should not abort the whole installation.
            }
            finally
            {
                var current = Interlocked.Increment(ref completed);
                progress?.Invoke(new DownloadProgress
                {
                    Phase = "下载文件",
                    Message = $"{current}/{total} · {Path.GetFileName(file.Path)}",
                    Percent = total == 0 ? 100 : current * 100d / total,
                    Completed = current,
                    Total = total
                });
                throttle.Release();
            }
        }).ToList();

        await Task.WhenAll(tasks);
        throttle.Dispose();
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

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
