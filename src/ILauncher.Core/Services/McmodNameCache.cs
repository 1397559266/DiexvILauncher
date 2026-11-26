using System.Text.Json;
using System.Text.RegularExpressions;

namespace ILauncher.Core.Services;

/// <summary>
/// mods 列表 / 下载列表用到的 MC百科中文名缓存，落在启动器 cache 目录，
/// 这样重开启动器时名字立刻可见，不必重新请求（MC百科对连续请求有限流）。
/// </summary>
public sealed class McmodNameCache
{
    private sealed class Entry
    {
        public string Name { get; set; } = "";
        public string? Url { get; set; }
        public DateTime SavedAt { get; set; } = DateTime.Now;
    }

    private static readonly Regex Noise = new(@"[^a-z0-9\u4e00-\u9fff]+", RegexOptions.Compiled);

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _loaded;
    private bool _dirty;

    public static string CachePath => Path.Combine(AppPaths.CacheDirectory, "mcmod-names.json");

    public int Count
    {
        get
        {
            lock (_gate)
            {
                EnsureLoaded();
                return _entries.Count;
            }
        }
    }

    public static string Normalize(string? title) =>
        string.IsNullOrWhiteSpace(title) ? "" : Noise.Replace(title.ToLowerInvariant(), "");

    public static bool HasChinese(string? text) =>
        !string.IsNullOrWhiteSpace(text) && text.Any(c => c >= 0x4E00 && c <= 0x9FFF);

    public bool TryGet(string? title, out string name, out string? url)
    {
        name = "";
        url = null;
        var key = Normalize(title);
        if (key.Length < 2)
        {
            return false;
        }

        lock (_gate)
        {
            EnsureLoaded();
            if (!_entries.TryGetValue(key, out var entry) || !HasChinese(entry.Name))
            {
                return false;
            }

            name = entry.Name;
            url = entry.Url;
            return true;
        }
    }

    public void Set(string? title, string name, string? url)
    {
        var key = Normalize(title);
        if (key.Length < 2 || !HasChinese(name))
        {
            return;
        }

        lock (_gate)
        {
            EnsureLoaded();
            _entries[key] = new Entry { Name = name, Url = url, SavedAt = DateTime.Now };
            _dirty = true;
        }
    }

    /// <summary>把改动落盘（批量写入后调用一次即可）。</summary>
    public void Flush()
    {
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
                File.WriteAllText(CachePath, JsonSerializer.Serialize(_entries,
                    new JsonSerializerOptions { WriteIndented = false }));
                _dirty = false;
            }
            catch
            {
                // 缓存写不进去不影响使用。
            }
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _entries.Clear();
            _loaded = true;
            _dirty = true;
        }

        Flush();
    }

    private void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        _loaded = true;
        try
        {
            if (File.Exists(CachePath))
            {
                _entries.Clear();
                var loaded = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath));
                if (loaded != null)
                {
                    foreach (var pair in loaded)
                    {
                        _entries[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch
        {
            _entries.Clear();
        }
    }
}
