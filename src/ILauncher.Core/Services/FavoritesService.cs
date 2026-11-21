using System.Text.Json;
using System.Text.Json.Serialization;

namespace ILauncher.Core.Services;

/// <summary>收藏的资源类型。</summary>
public static class FavoriteKinds
{
    public const string Mod = "mod";
    public const string Modpack = "modpack";
    public const string Shader = "shader";
    public const string ResourcePack = "resourcepack";

    public static string Label(string kind) => kind switch
    {
        Mod => "模组",
        Modpack => "整合包",
        Shader => "光影包",
        ResourcePack => "资源包",
        _ => kind
    };
}

public sealed class FavoriteEntry
{
    /// <summary>唯一键：<c>{source}:{projectId}</c>。</summary>
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("kind")]
    public string Kind { get; set; } = FavoriteKinds.Mod;

    /// <summary>Modrinth 或 CurseForge。</summary>
    [JsonPropertyName("source")]
    public string Source { get; set; } = "Modrinth";

    /// <summary>Modrinth 项目 id/slug，或 CurseForge 的 <c>cf:123</c>。</summary>
    [JsonPropertyName("projectId")]
    public string ProjectId { get; set; } = "";

    [JsonPropertyName("title")]
    public string Title { get; set; } = "";

    [JsonPropertyName("slug")]
    public string Slug { get; set; } = "";

    [JsonPropertyName("iconUrl")]
    public string? IconUrl { get; set; }

    [JsonPropertyName("savedAt")]
    public DateTime SavedAt { get; set; } = DateTime.Now;

    /// <summary>开启「收藏时保存文件」时记录本地文件路径。</summary>
    [JsonPropertyName("localFile")]
    public string? LocalFile { get; set; }

    [JsonPropertyName("fileName")]
    public string? FileName { get; set; }

    [JsonIgnore]
    public string KindLabel => FavoriteKinds.Label(Kind);

    [JsonIgnore]
    public bool HasLocalFile => !string.IsNullOrWhiteSpace(LocalFile) && File.Exists(LocalFile);

    [JsonIgnore]
    public string SourceLabel => Source;
}

/// <summary>
/// 收藏数据存在启动器目录（favorites.json）；开启保存文件时，文件放在 favorites/files 下。
/// </summary>
public sealed class FavoritesService
{
    private readonly object _gate = new();
    private List<FavoriteEntry> _items = new();
    private bool _loaded;

    private static string StorePath => Path.Combine(AppPaths.DataDirectory, "favorites.json");

    public static string FilesDirectory => Path.Combine(AppPaths.DataDirectory, "favorites", "files");

    public IReadOnlyList<FavoriteEntry> All
    {
        get
        {
            EnsureLoaded();
            lock (_gate)
            {
                return _items.OrderByDescending(item => item.SavedAt).ToList();
            }
        }
    }

    public bool Contains(string source, string projectId)
    {
        var id = BuildId(source, projectId);
        lock (_gate)
        {
            EnsureLoaded();
            return _items.Any(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
        }
    }

    /// <summary>加入收藏；返回 true 表示新加入，false 表示已存在（会被更新）。</summary>
    public bool Add(FavoriteEntry entry)
    {
        EnsureLoaded();
        entry.Id = string.IsNullOrWhiteSpace(entry.Id)
            ? BuildId(entry.Source, entry.ProjectId)
            : entry.Id;
        lock (_gate)
        {
            var existing = _items.FirstOrDefault(item =>
                string.Equals(item.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Title = entry.Title;
                existing.IconUrl = entry.IconUrl;
                existing.Kind = entry.Kind;
                existing.Slug = entry.Slug;
                if (!string.IsNullOrWhiteSpace(entry.LocalFile))
                {
                    existing.LocalFile = entry.LocalFile;
                    existing.FileName = entry.FileName;
                }

                Save();
                return false;
            }

            entry.SavedAt = DateTime.Now;
            _items.Add(entry);
            Save();
            return true;
        }
    }

    public bool Remove(string id)
    {
        EnsureLoaded();
        lock (_gate)
        {
            var removed = _items.RemoveAll(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
            if (removed)
            {
                Save();
            }

            return removed;
        }
    }

    public void SaveLocalFile(string id, string path, string? fileName)
    {
        lock (_gate)
        {
            var item = _items.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                return;
            }

            item.LocalFile = path;
            item.FileName = fileName;
            Save();
        }
    }

    private static string BuildId(string source, string projectId) => $"{source}:{projectId}";

    private void EnsureLoaded()
    {
        lock (_gate)
        {
            if (_loaded)
            {
                return;
            }

            _loaded = true;
            try
            {
                if (File.Exists(StorePath))
                {
                    _items = JsonSerializer.Deserialize<List<FavoriteEntry>>(File.ReadAllText(StorePath)) ?? new();
                }
            }
            catch
            {
                _items = new();
            }
        }
    }

    private void Save()
    {
        try
        {
            AppPaths.EnsureCreated();
            Directory.CreateDirectory(Path.GetDirectoryName(StorePath)!);
            File.WriteAllText(StorePath, JsonSerializer.Serialize(_items,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 收藏写不进去不影响使用。
        }
    }
}
