using System.Globalization;
using System.Text.Json;

namespace ILauncher.Core.Services;

/// <summary>
/// CurseForge 内容源，走 MCIM 镜像（mod.mcimirror.top），因此不需要 CurseForge API key。
/// MCIM 的响应结构与官方 CurseForge API 一致（外层 data 包装），所以换成自建 key 时
/// 只需要把 BaseUrl 换成 https://api.curseforge.com/v1 并补上 x-api-key。
/// </summary>
public sealed class CurseForgeService
{
    private const string BaseUrl = "https://mod.mcimirror.top/curseforge/v1";
    private const int GameIdMinecraft = 432;
    private const int ClassIdMods = 6;

    private static readonly string[] LoaderNames = ["Forge", "NeoForge", "Fabric", "Quilt"];

    public static bool IsAvailable { get; set; } = true;

    /// <summary>按关键词搜索模组。</summary>
    public async Task<(IReadOnlyList<ModrinthProject> Items, int Total)> SearchAsync(string query,
        string? gameVersion, string? loader, int offset, int pageSize,
        CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>
        {
            $"gameId={GameIdMinecraft}",
            $"classId={ClassIdMods}",
            $"index={Math.Max(0, offset)}",
            $"pageSize={Math.Clamp(pageSize, 1, 50)}",
            "sortField=2",
            "sortOrder=desc"
        };
        if (!string.IsNullOrWhiteSpace(query))
        {
            parameters.Add("searchFilter=" + Uri.EscapeDataString(query));
        }

        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            parameters.Add("gameVersion=" + Uri.EscapeDataString(gameVersion));
        }

        if (ToLoaderType(loader) is { } loaderType)
        {
            parameters.Add("modLoaderType=" + loaderType);
        }

        var url = $"{BaseUrl}/mods/search?{string.Join('&', parameters)}";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (document == null || !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return ([], 0);
        }

        var items = new List<ModrinthProject>();
        foreach (var element in data.EnumerateArray())
        {
            var project = MapProject(element);
            if (project != null)
            {
                items.Add(project);
            }
        }

        var total = items.Count;
        if (document.RootElement.TryGetProperty("pagination", out var pagination) &&
            pagination.ValueKind == JsonValueKind.Object &&
            pagination.TryGetProperty("totalCount", out var totalCount) &&
            totalCount.TryGetInt32(out var parsed))
        {
            total = parsed;
        }

        return (items, total);
    }

    /// <summary>读取某个模组的文件列表，并转换成统一的版本模型。</summary>
    public async Task<IReadOnlyList<ModrinthVersion>> GetFilesAsync(long modId, string? gameVersion,
        string? loader, CancellationToken cancellationToken = default)
    {
        var parameters = new List<string> { "pageSize=50", "index=0" };
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            parameters.Add("gameVersion=" + Uri.EscapeDataString(gameVersion));
        }

        if (ToLoaderType(loader) is { } loaderType)
        {
            parameters.Add("modLoaderType=" + loaderType);
        }

        var url = $"{BaseUrl}/mods/{modId}/files?{string.Join('&', parameters)}";
        using var document = await GetJsonAsync(url, cancellationToken).ConfigureAwait(false);
        if (document == null || !document.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var versions = new List<ModrinthVersion>();
        foreach (var element in data.EnumerateArray())
        {
            var version = MapFile(element, modId);
            if (version != null)
            {
                versions.Add(version);
            }
        }

        return versions.OrderByDescending(v => v.DatePublished).ToList();
    }

    private static ModrinthProject? MapProject(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var id = element.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var modId) ? modId : 0;
        var name = ReadString(element, "name");
        if (id == 0 || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var slug = ReadString(element, "slug") ?? name.ToLowerInvariant().Replace(' ', '-');
        var summary = ReadString(element, "summary") ?? "";
        string? icon = null;
        if (element.TryGetProperty("logo", out var logo) && logo.ValueKind == JsonValueKind.Object)
        {
            icon = ReadString(logo, "thumbnailUrl") ?? ReadString(logo, "url");
        }

        var downloads = element.TryGetProperty("downloadCount", out var downloadElement) &&
                        downloadElement.TryGetInt64(out var downloadCount)
            ? downloadCount
            : 0;

        var categories = new List<string>();
        if (element.TryGetProperty("categories", out var categoryArray) &&
            categoryArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var category in categoryArray.EnumerateArray())
            {
                var categoryName = ReadString(category, "name");
                if (!string.IsNullOrWhiteSpace(categoryName) && categories.Count < 6)
                {
                    categories.Add(categoryName!);
                }
            }
        }

        return new ModrinthProject($"cf:{id}", slug, name, summary, icon, downloads, "mod", categories)
        {
            CurseForgeModId = id,
            HasModrinth = false
        };
    }

    private static ModrinthVersion? MapFile(JsonElement element, long modId)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var fileId = element.TryGetProperty("id", out var idElement) && idElement.TryGetInt64(out var parsedId)
            ? parsedId
            : 0;
        var fileName = ReadString(element, "fileName") ?? "";
        var downloadUrl = ReadString(element, "downloadUrl") ?? "";
        if (fileId == 0 || string.IsNullOrWhiteSpace(fileName) || string.IsNullOrWhiteSpace(downloadUrl))
        {
            return null;
        }

        var displayName = ReadString(element, "displayName") ?? fileName;
        var releaseType = element.TryGetProperty("releaseType", out var releaseElement) &&
                          releaseElement.TryGetInt32(out var parsedRelease)
            ? parsedRelease switch
            {
                2 => "beta",
                3 => "alpha",
                _ => "release"
            }
            : "release";

        DateTime published = DateTime.MinValue;
        var dateText = ReadString(element, "fileDate");
        if (!string.IsNullOrWhiteSpace(dateText) &&
            DateTime.TryParse(dateText, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal,
                out var parsedDate))
        {
            published = parsedDate;
        }

        var gameVersions = new List<string>();
        var loaders = new List<string>();
        if (element.TryGetProperty("gameVersions", out var gameVersionArray) &&
            gameVersionArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in gameVersionArray.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = item.GetString() ?? "";
                if (LoaderNames.Contains(value, StringComparer.OrdinalIgnoreCase))
                {
                    loaders.Add(NormalizeLoader(value));
                }
                else if (value.Length > 0)
                {
                    gameVersions.Add(value);
                }
            }
        }

        string? sha1 = null;
        if (element.TryGetProperty("hashes", out var hashArray) && hashArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var hash in hashArray.EnumerateArray())
            {
                if (hash.TryGetProperty("algo", out var algo) && algo.TryGetInt32(out var algoValue) &&
                    algoValue == 1 && hash.TryGetProperty("value", out var valueElement))
                {
                    sha1 = valueElement.GetString();
                }
            }
        }

        var size = element.TryGetProperty("fileLength", out var lengthElement) &&
                   lengthElement.TryGetInt64(out var fileLength)
            ? fileLength
            : 0;

        var dependencies = new List<ModrinthDependency>();
        if (element.TryGetProperty("dependencies", out var dependencyArray) &&
            dependencyArray.ValueKind == JsonValueKind.Array)
        {
            foreach (var dependency in dependencyArray.EnumerateArray())
            {
                var relation = dependency.TryGetProperty("relationType", out var relationElement) &&
                               relationElement.TryGetInt32(out var relationValue)
                    ? relationValue
                    : 0;
                if (relation != 3)
                {
                    continue;
                }

                var dependencyModId = dependency.TryGetProperty("modId", out var modIdElement) &&
                                      modIdElement.TryGetInt64(out var parsedModId)
                    ? parsedModId
                    : 0;
                dependencies.Add(new ModrinthDependency(null, dependencyModId > 0 ? $"cf:{dependencyModId}" : null,
                    null, "required"));
            }
        }

        return new ModrinthVersion(
            $"cf-file:{fileId}",
            $"cf:{modId}",
            displayName,
            displayName,
            releaseType,
            published,
            gameVersions.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            loaders.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            new ModrinthFile(downloadUrl, fileName, size, true, sha1),
            dependencies,
            "CurseForge");
    }

    private static async Task<JsonDocument?> GetJsonAsync(string url, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", "DiexvILauncher/1.3.1");
        using var response = await DownloadHelper.HttpClient.SendAsync(request, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(body))
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static int? ToLoaderType(string? loader) => loader?.ToLowerInvariant() switch
    {
        "forge" => 1,
        "fabric" => 4,
        "quilt" => 5,
        "neoforge" => 6,
        _ => null
    };

    private static string NormalizeLoader(string loader) => loader.ToLowerInvariant() switch
    {
        "forge" => "Forge",
        "fabric" => "Fabric",
        "quilt" => "Quilt",
        "neoforge" => "NeoForge",
        _ => loader
    };

    private static string? ReadString(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}

/// <summary>把两个内容源的结果合并成一份列表。</summary>
public static class ModCatalog
{
    /// <summary>
    /// 按 slug 归一化后合并：同一个模组在 Modrinth 和 CurseForge 上通常使用相同 slug。
    /// 合并后的条目保留 Modrinth 的展示信息，并标记 CurseForge 的 mod id。
    /// </summary>
    public static IReadOnlyList<ModrinthProject> Merge(IReadOnlyList<ModrinthProject> modrinth,
        IReadOnlyList<ModrinthProject> curseForge)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var result = new List<ModrinthProject>(modrinth.Count + curseForge.Count);

        foreach (var project in modrinth)
        {
            var key = KeyOf(project);
            if (key.Length == 0 || index.ContainsKey(key))
            {
                continue;
            }

            index[key] = result.Count;
            result.Add(project);
        }

        foreach (var project in curseForge)
        {
            var key = KeyOf(project);
            if (key.Length == 0)
            {
                continue;
            }

            if (index.TryGetValue(key, out var position))
            {
                var existing = result[position];
                result[position] = existing with
                {
                    CurseForgeModId = project.CurseForgeModId,
                    HasModrinth = true
                };
                continue;
            }

            index[key] = result.Count;
            result.Add(project);
        }

        return result;
    }

    private static string KeyOf(ModrinthProject project)
    {
        var source = string.IsNullOrWhiteSpace(project.Slug) ? project.Title : project.Slug;
        return new string(source.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
    }
}
