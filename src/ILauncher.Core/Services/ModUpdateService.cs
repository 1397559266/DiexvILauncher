using System.Net;
using System.Text;
using System.Text.Json;

namespace ILauncher.Core.Services;

/// <summary>某个模组文件的最新版本信息，可写入本地缓存。</summary>
public sealed record ModUpdateState(
    string Sha1,
    string ProjectId,
    string ProjectTitle,
    string? IconUrl,
    string LatestVersionId,
    string LatestVersionNumber,
    string? LatestFileName,
    string? LatestDownloadUrl,
    string? LatestSha1,
    DateTime CheckedAt,
    bool UpdateAvailable);

/// <summary>
/// 通过 Modrinth 的哈希接口批量识别本地模组，并查询最新版本；
/// 识别不到 Modrinth 项目的模组再退回 CurseForge 镜像取图标。
/// </summary>
public sealed class ModUpdateService
{
    private const string BaseUrl = "https://api.modrinth.com/v2";

    private readonly CurseForgeService _curseForge = new();

    public async Task<Dictionary<string, ModUpdateState>> CheckAsync(
        IReadOnlyList<(string Sha1, string Path, string Name, string Version)> mods,
        string? gameVersion, string? loader, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, ModUpdateState>(StringComparer.OrdinalIgnoreCase);
        if (mods.Count == 0)
        {
            return result;
        }

        var hashes = mods.Select(m => m.Sha1).Where(h => h.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var versions = await LookupVersionsAsync(hashes, cancellationToken).ConfigureAwait(false);
        var projects = await LookupProjectsAsync(versions.Values.Select(v => v.ProjectId).Distinct().ToList(),
            cancellationToken).ConfigureAwait(false);

        var latestByProject = await LoadLatestVersionsAsync(projects.Keys.ToList(), gameVersion, loader,
            cancellationToken).ConfigureAwait(false);

        foreach (var mod in mods)
        {
            if (!versions.TryGetValue(mod.Sha1, out var current) ||
                !projects.TryGetValue(current.ProjectId, out var project))
            {
                continue;
            }

            latestByProject.TryGetValue(current.ProjectId, out var latest);
            var updateAvailable = latest != null &&
                                  latest.File != null &&
                                  !string.Equals(latest.File.Sha1, mod.Sha1, StringComparison.OrdinalIgnoreCase) &&
                                  !string.Equals(latest.VersionNumber, mod.Version, StringComparison.OrdinalIgnoreCase);

            result[mod.Sha1] = new ModUpdateState(
                mod.Sha1,
                current.ProjectId,
                project.Title,
                project.IconUrl,
                latest?.Id ?? current.Id,
                latest?.VersionNumber ?? current.VersionNumber,
                latest?.File?.FileName,
                latest?.File?.Url,
                latest?.File?.Sha1,
                DateTime.Now,
                updateAvailable);
        }

        // Modrinth 没识别出来的模组，用 CurseForge 镜像按名称精确匹配后查文件列表。
        var missing = mods.Where(m => !result.ContainsKey(m.Sha1)).Take(60).ToList();
        using var gate = new SemaphoreSlim(4);
        var curseTasks = missing.Select(async mod =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var (items, _) = await _curseForge.SearchAsync(mod.Name, null, null, 0, 5, cancellationToken)
                    .ConfigureAwait(false);
                var match = items.FirstOrDefault(item =>
                    string.Equals(item.Title.Trim(), mod.Name.Trim(), StringComparison.OrdinalIgnoreCase));
                if (match?.CurseForgeModId is not > 0)
                {
                    return;
                }

                var files = await _curseForge.GetFilesAsync(match.CurseForgeModId.Value, gameVersion, loader,
                    cancellationToken).ConfigureAwait(false);
                var latest = files.FirstOrDefault(file => file.File != null);

                var updated = false;
                var localTime = File.GetLastWriteTime(mod.Path);
                if (latest?.File != null)
                {
                    var sameFile = string.Equals(latest.File.FileName, Path.GetFileName(mod.Path),
                        StringComparison.OrdinalIgnoreCase);
                    updated = !sameFile && latest.DatePublished > localTime;
                }

                lock (result)
                {
                    result[mod.Sha1] = new ModUpdateState(
                        mod.Sha1,
                        $"cf:{match.CurseForgeModId.Value}",
                        match.Title,
                        match.IconUrl,
                        latest?.Id ?? "",
                        updated ? latest!.VersionNumber : mod.Version,
                        latest?.File?.FileName,
                        latest?.File?.Url,
                        latest?.File?.Sha1,
                        DateTime.Now,
                        updated);
                }
            }
            catch
            {
                // 单个模组的 CurseForge 兜底失败不影响其它结果。
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(curseTasks).ConfigureAwait(false);

        return result;
    }

    private static async Task<Dictionary<string, ModrinthVersion>> LookupVersionsAsync(IReadOnlyList<string> hashes,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in hashes.Chunk(100))
        {
            var payload = JsonSerializer.Serialize(new
            {
                hashes = chunk,
                algorithm = "sha1"
            });
            using var content = new StringContent(payload, Encoding.UTF8, "application/json");
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/version_files")
            {
                Content = content
            };
            using var response = await DownloadHelper.HttpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            // /version_files 返回的是 { "<sha1>": { ...版本... } }，按哈希键取值。
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in document.RootElement.EnumerateObject())
            {
                var element = property.Value;
                var sha1 = property.Name;
                var projectId = ReadString(element, "project_id");
                var versionNumber = ReadString(element, "version_number");
                if (string.IsNullOrWhiteSpace(sha1) || string.IsNullOrWhiteSpace(projectId))
                {
                    continue;
                }

                var file = ReadPrimaryFile(element);
                map[sha1] = new ModrinthVersion(sha1, projectId, ReadString(element, "name") ?? versionNumber ?? "",
                    versionNumber ?? "", "release", DateTime.MinValue, [], [], file, []);
            }
        }

        return map;
    }

    private static async Task<Dictionary<string, (string Title, string? IconUrl)>> LookupProjectsAsync(
        IReadOnlyList<string> projectIds, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, (string Title, string? IconUrl)>(StringComparer.Ordinal);
        foreach (var chunk in projectIds.Chunk(50))
        {
            var ids = JsonSerializer.Serialize(chunk);
            using var response = await DownloadHelper.HttpClient.GetAsync(
                $"{BaseUrl}/projects?ids={Uri.EscapeDataString(ids)}", cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                continue;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var element in document.RootElement.EnumerateArray())
            {
                var id = ReadString(element, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                map[id] = (ReadString(element, "title") ?? "", ReadString(element, "icon_url"));
            }
        }

        return map;
    }

    private static async Task<Dictionary<string, ModrinthVersion>> LoadLatestVersionsAsync(
        IReadOnlyList<string> projectIds, string? gameVersion, string? loader, CancellationToken cancellationToken)
    {
        var map = new Dictionary<string, ModrinthVersion>(StringComparer.Ordinal);
        using var gate = new SemaphoreSlim(6);
        var tasks = projectIds.Select(async projectId =>
        {
            await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var query = new List<string>();
                if (!string.IsNullOrWhiteSpace(gameVersion))
                {
                    query.Add("game_versions=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion })));
                }

                if (!string.IsNullOrWhiteSpace(loader))
                {
                    query.Add("loaders=" + Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader })));
                }

                var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version" +
                          (query.Count > 0 ? "?" + string.Join('&', query) : "");
                using var response = await DownloadHelper.HttpClient.GetAsync(url, cancellationToken)
                    .ConfigureAwait(false);
                if (!response.IsSuccessStatusCode)
                {
                    return;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.ValueKind != JsonValueKind.Array ||
                    document.RootElement.GetArrayLength() == 0)
                {
                    return;
                }

                var element = document.RootElement[0];
                var file = ReadPrimaryFile(element);
                if (file == null)
                {
                    return;
                }

                var version = new ModrinthVersion(
                    ReadString(element, "id") ?? "",
                    projectId,
                    ReadString(element, "name") ?? "",
                    ReadString(element, "version_number") ?? "",
                    ReadString(element, "version_type") ?? "release",
                    DateTime.TryParse(ReadString(element, "date_published"), out var published) ? published : DateTime.MinValue,
                    [], [], file, []);
                lock (map)
                {
                    map[projectId] = version;
                }
            }
            catch
            {
                // 单个项目失败不影响其它模组。
            }
            finally
            {
                gate.Release();
            }
        });

        await Task.WhenAll(tasks).ConfigureAwait(false);
        return map;
    }

    private static ModrinthFile? ReadPrimaryFile(JsonElement version)
    {
        if (!version.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var file in files.EnumerateArray())
        {
            var primary = file.TryGetProperty("primary", out var primaryElement) && primaryElement.ValueKind == JsonValueKind.True;
            var url = ReadString(file, "url");
            var fileName = ReadString(file, "filename");
            if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(fileName))
            {
                continue;
            }

            string? sha1 = null;
            if (file.TryGetProperty("hashes", out var hashes) && hashes.ValueKind == JsonValueKind.Object)
            {
                sha1 = ReadString(hashes, "sha1");
            }

            var size = file.TryGetProperty("size", out var sizeElement) && sizeElement.TryGetInt64(out var parsed) ? parsed : 0;
            var result = new ModrinthFile(url, fileName, size, primary, sha1);
            if (primary)
            {
                return result;
            }
        }

        return null;
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
