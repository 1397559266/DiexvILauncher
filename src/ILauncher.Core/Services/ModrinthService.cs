using System.Net;
using System.Text.Json;

namespace ILauncher.Core.Services;

public sealed record ModrinthProject(
    string Id,
    string Slug,
    string Title,
    string Description,
    string? IconUrl,
    long Downloads,
    string ProjectType,
    IReadOnlyList<string> Categories)
{
    public static bool PreferChineseNames { get; set; } = true;

    public string? ChineseName { get; init; }
    public string? McmodUrl { get; init; }
    public string DisplayTitle => !PreferChineseNames || string.IsNullOrWhiteSpace(ChineseName)
        ? Title
        : ChineseName;
    public bool HasMcmod => !string.IsNullOrWhiteSpace(McmodUrl);
    public string ModrinthUrl
    {
        get
        {
            var kind = ProjectType switch
            {
                "resourcepack" => "resourcepack",
                "shader" => "shader",
                "modpack" => "modpack",
                _ => "mod"
            };
            return $"https://modrinth.com/{kind}/{Uri.EscapeDataString(Slug)}";
        }
    }
    public string CurseForgeUrl
    {
        get
        {
            var className = ProjectType switch
            {
                "resourcepack" => "texture-packs",
                "shader" => "shaders",
                "modpack" => "modpacks",
                _ => "mc-mods"
            };
            return $"https://www.curseforge.com/minecraft/search?search={Uri.EscapeDataString(Title)}&class={className}";
        }
    }

    public string DownloadsText => Downloads switch
    {
        >= 1_000_000 => $"{Downloads / 1_000_000d:0.#}M 下载",
        >= 1_000 => $"{Downloads / 1_000d:0.#}K 下载",
        _ => $"{Downloads} 下载"
    };

    public string CategoryText => string.Join(" · ", Categories.Take(3));
}

public sealed record ModrinthGalleryImage(string Url, string Title, bool Featured);

public sealed record ModrinthProjectDetails(
    string Id,
    string Title,
    string Body,
    string? IssuesUrl,
    string? SourceUrl,
    string? WikiUrl,
    IReadOnlyList<ModrinthGalleryImage> Gallery);

public sealed record ModrinthFile(string Url, string FileName, long Size, bool Primary, string? Sha1);

public sealed record ModrinthSearchResult(
    IReadOnlyList<ModrinthProject> Hits,
    int TotalHits,
    int Offset,
    int Limit);

public sealed record ModrinthDependency(
    string? VersionId,
    string? ProjectId,
    string? FileName,
    string DependencyType);

public sealed record ModrinthVersion(
    string Id,
    string ProjectId,
    string Name,
    string VersionNumber,
    string ReleaseType,
    DateTime DatePublished,
    IReadOnlyList<string> GameVersions,
    IReadOnlyList<string> Loaders,
    ModrinthFile? File,
    IReadOnlyList<ModrinthDependency> Dependencies)
{
    public string GameVersionsText => string.Join(", ", GameVersions.Take(6));
    public string LoadersText => string.Join(", ", Loaders);
    public string ReleaseTypeText => ReleaseType switch
    {
        "release" => ModrinthProject.PreferChineseNames ? "正式版" : "Release",
        "beta" => "Beta",
        "alpha" => "Alpha",
        _ => ReleaseType
    };
    public string FileName => File?.FileName ?? "";
}

public sealed class ModrinthService
{
    private const string BaseUrl = "https://api.modrinth.com/v2";

    public async Task<ModrinthSearchResult> SearchAsync(string query, string projectType,
        string? gameVersion, string? loader, string? category = null, string index = "relevance",
        int offset = 0, int limit = 20, CancellationToken cancellationToken = default)
    {
        var facets = new List<List<string>> { new() { $"project_type:{projectType}" } };
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            facets.Add(new List<string> { $"versions:{gameVersion}" });
        }

        if (!string.IsNullOrWhiteSpace(loader))
        {
            facets.Add(new List<string> { $"categories:{loader.ToLowerInvariant()}" });
        }

        if (!string.IsNullOrWhiteSpace(category) &&
            !string.Equals(category, "all", StringComparison.OrdinalIgnoreCase))
        {
            facets.Add(new List<string> { $"categories:{category.ToLowerInvariant()}" });
        }

        var facetsJson = JsonSerializer.Serialize(facets);
        var sortIndex = index is "downloads" or "follows" or "newest" or "updated" ? index : "relevance";
        var url = $"{BaseUrl}/search?query={Uri.EscapeDataString(query ?? "")}&limit={limit}&offset={offset}" +
                  $"&facets={Uri.EscapeDataString(facetsJson)}&index={sortIndex}";
        var json = await DownloadHelper.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var result = new List<ModrinthProject>();
        if (!document.RootElement.TryGetProperty("hits", out var hits))
        {
            return new ModrinthSearchResult(result, 0, offset, limit);
        }

        var totalHits = document.RootElement.TryGetProperty("total_hits", out var total) &&
                        total.TryGetInt32(out var parsedTotal)
            ? parsedTotal
            : 0;

        foreach (var hit in hits.EnumerateArray())
        {
            result.Add(new ModrinthProject(
                hit.TryGetProperty("project_id", out var id) ? id.GetString() ?? "" : "",
                hit.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
                hit.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                hit.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
                hit.TryGetProperty("icon_url", out var icon) ? icon.GetString() : null,
                hit.TryGetProperty("downloads", out var downloads) && downloads.TryGetInt64(out var count) ? count : 0,
                hit.TryGetProperty("project_type", out var type) ? type.GetString() ?? projectType : projectType,
                hit.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array
                    ? categories.EnumerateArray().Select(c => c.GetString() ?? "").Where(c => c.Length > 0).ToList()
                    : new List<string>()));
        }

        return new ModrinthSearchResult(result, Math.Max(totalHits, result.Count), offset, limit);
    }

    public async Task<ModrinthProject?> GetProjectAsync(string idOrSlug,
        CancellationToken cancellationToken = default)
    {
        var json = await DownloadHelper.GetStringAsync(
            $"{BaseUrl}/project/{Uri.EscapeDataString(idOrSlug)}", cancellationToken);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return new ModrinthProject(
            id,
            root.TryGetProperty("slug", out var slug) ? slug.GetString() ?? "" : "",
            root.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
            root.TryGetProperty("description", out var description) ? description.GetString() ?? "" : "",
            root.TryGetProperty("icon_url", out var icon) ? icon.GetString() : null,
            root.TryGetProperty("downloads", out var downloads) && downloads.TryGetInt64(out var count) ? count : 0,
            root.TryGetProperty("project_type", out var type) ? type.GetString() ?? "mod" : "mod",
            root.TryGetProperty("categories", out var categories) && categories.ValueKind == JsonValueKind.Array
                ? categories.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToList()
                : new List<string>());
    }

    public async Task<List<ModrinthVersion>> GetVersionsAsync(string projectId, string? gameVersion,
        string? loader, CancellationToken cancellationToken = default)
    {
        var query = new List<string>();
        if (!string.IsNullOrWhiteSpace(gameVersion))
        {
            query.Add($"game_versions={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion }))}");
        }

        if (!string.IsNullOrWhiteSpace(loader))
        {
            query.Add($"loaders={Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader.ToLowerInvariant() }))}");
        }

        var url = $"{BaseUrl}/project/{Uri.EscapeDataString(projectId)}/version";
        if (query.Count > 0)
        {
            url += "?" + string.Join("&", query);
        }

        var json = await DownloadHelper.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(json);
        var result = new List<ModrinthVersion>();
        foreach (var element in document.RootElement.EnumerateArray())
        {
            result.Add(ParseVersion(element));
        }

        return result
            .OrderByDescending(v => v.ReleaseType == "release")
            .ThenByDescending(v => v.DatePublished)
            .ToList();
    }

    public async Task<ModrinthVersion?> GetVersionAsync(string versionId,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await DownloadHelper.GetStringAsync(
                $"{BaseUrl}/version/{Uri.EscapeDataString(versionId)}", cancellationToken);
            using var document = JsonDocument.Parse(json);
            return ParseVersion(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    public async Task<ModrinthVersion?> GetVersionByHashAsync(string sha1, CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await DownloadHelper.GetStringAsync(
                $"{BaseUrl}/version_file/{Uri.EscapeDataString(sha1)}?algorithm=sha1", cancellationToken);
            using var document = JsonDocument.Parse(json);
            return ParseVersion(document.RootElement);
        }
        catch
        {
            return null;
        }
    }

    private static ModrinthVersion ParseVersion(JsonElement element)
    {
        var files = element.TryGetProperty("files", out var fileArray)
            ? fileArray.EnumerateArray().ToList()
            : new List<JsonElement>();
        var primary = files.FirstOrDefault(file => file.TryGetProperty("primary", out var p) && p.ValueKind == JsonValueKind.True);
        if (primary.ValueKind == JsonValueKind.Undefined)
        {
            primary = files.FirstOrDefault();
        }

        ModrinthFile? file = null;
        if (primary.ValueKind != JsonValueKind.Undefined &&
            primary.TryGetProperty("url", out var fileUrl) &&
            primary.TryGetProperty("filename", out var fileName))
        {
            string? sha1 = null;
            if (primary.TryGetProperty("hashes", out var hashes) &&
                hashes.TryGetProperty("sha1", out var hashValue))
            {
                sha1 = hashValue.GetString();
            }

            file = new ModrinthFile(fileUrl.GetString() ?? "", fileName.GetString() ?? "",
                primary.TryGetProperty("size", out var size) && size.TryGetInt64(out var sizeValue) ? sizeValue : 0,
                true, sha1);
        }

        return new ModrinthVersion(
            GetString(element, "id"),
            GetString(element, "project_id"),
            GetString(element, "name"),
            GetString(element, "version_number"),
            GetString(element, "version_type"),
            DateTime.TryParse(GetString(element, "date_published"), out var date) ? date : DateTime.MinValue,
            GetStringArray(element, "game_versions"),
            GetStringArray(element, "loaders"),
            file,
            ParseDependencies(element));
    }

    private static List<ModrinthDependency> ParseDependencies(JsonElement element)
    {
        if (!element.TryGetProperty("dependencies", out var dependencies) ||
            dependencies.ValueKind != JsonValueKind.Array)
        {
            return new List<ModrinthDependency>();
        }

        var result = new List<ModrinthDependency>();
        foreach (var dependency in dependencies.EnumerateArray())
        {
            var projectId = GetString(dependency, "project_id");
            var versionId = GetString(dependency, "version_id");
            if (string.IsNullOrWhiteSpace(projectId) && string.IsNullOrWhiteSpace(versionId))
            {
                continue;
            }

            result.Add(new ModrinthDependency(
                string.IsNullOrWhiteSpace(versionId) ? null : versionId,
                string.IsNullOrWhiteSpace(projectId) ? null : projectId,
                GetString(dependency, "file_name") is { Length: > 0 } fileName ? fileName : null,
                GetString(dependency, "dependency_type")));
        }

        return result;
    }

    public async Task<ModrinthProjectDetails?> GetProjectDetailsAsync(string idOrSlug,
        CancellationToken cancellationToken = default)
    {
        try
        {
            var json = await DownloadHelper.GetStringAsync(
                $"{BaseUrl}/project/{Uri.EscapeDataString(idOrSlug)}", cancellationToken);
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            var id = root.TryGetProperty("id", out var idElement) ? idElement.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                return null;
            }

            string? OptionalUrl(string name)
                => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
                    ? value.GetString()
                    : null;

            var gallery = new List<ModrinthGalleryImage>();
            if (root.TryGetProperty("gallery", out var galleryElement) &&
                galleryElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var image in galleryElement.EnumerateArray())
                {
                    var url = image.TryGetProperty("url", out var urlElement) ? urlElement.GetString() : null;
                    if (string.IsNullOrWhiteSpace(url))
                    {
                        continue;
                    }

                    gallery.Add(new ModrinthGalleryImage(
                        url,
                        image.TryGetProperty("title", out var titleElement) ? titleElement.GetString() ?? "" : "",
                        image.TryGetProperty("featured", out var featuredElement) &&
                        featuredElement.ValueKind == JsonValueKind.True));
                }
            }

            return new ModrinthProjectDetails(
                id,
                root.TryGetProperty("title", out var title) ? title.GetString() ?? "" : "",
                root.TryGetProperty("body", out var body) ? body.GetString() ?? "" : "",
                OptionalUrl("issues_url"),
                OptionalUrl("source_url"),
                OptionalUrl("wiki_url"),
                gallery);
        }
        catch
        {
            return null;
        }
    }

    public async Task<string> InstallAsync(ModrinthProject project, string destinationDirectory, string? gameVersion,
        string? loader, Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        progress?.Invoke(new DownloadProgress { Phase = "解析版本", Message = $"正在获取 {project.Title} 的版本…", Percent = 5 });
        var versions = await GetVersionsAsync(project.Id, gameVersion, loader, cancellationToken);
        var version = versions.FirstOrDefault();
        if (version?.File == null)
        {
            throw new InvalidOperationException("该资源没有适用于当前版本的文件。");
        }

        return await InstallVersionAsync(project, version, destinationDirectory, progress, cancellationToken);
    }

    public async Task<string> InstallVersionAsync(ModrinthProject project, ModrinthVersion version,
        string destinationDirectory, Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (version.File == null)
        {
            throw new InvalidOperationException("该版本没有可下载文件。");
        }

        Directory.CreateDirectory(destinationDirectory);
        var target = Path.Combine(destinationDirectory, Sanitize(version.File.FileName));
        await DownloadHelper.DownloadFileAsync(version.File.Url, target, (completed, total) =>
        {
            progress?.Invoke(new DownloadProgress
            {
                Phase = "下载",
                Message = total > 0
                    ? $"{version.File.FileName} · {completed / 1024d / 1024d:0.0}/{total / 1024d / 1024d:0.0} MB"
                    : $"{version.File.FileName} · {completed / 1024d:0} KB",
                Percent = total > 0 ? completed * 100d / total : -1,
                Completed = completed,
                Total = total
            });
        }, cancellationToken);
        progress?.Invoke(new DownloadProgress { Phase = "完成", Message = $"已安装 {project.Title}", Percent = 100 });
        return target;
    }

    private static string GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static List<string> GetStringArray(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(item => item.GetString() ?? "").Where(item => item.Length > 0).ToList()
            : new List<string>();

    private static string Sanitize(string name)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(invalid, '_');
        }

        return name;
    }
}
