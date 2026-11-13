using System.Security.Cryptography;
using System.Text.Json;

namespace ILauncher.Core.Services;

/// <summary>
/// 按“路径 + 大小 + 修改时间”缓存 jar 的 SHA-1，避免每次进入模组管理都重新读一遍全部文件。
/// 只在后台线程调用。
/// </summary>
public static class ModHashCache
{
    private sealed record Entry(long Length, long Ticks, string Sha1);

    private static readonly object Gate = new();
    private static readonly Dictionary<string, Entry> Entries = Load();
    private static bool _dirty;

    private static string CachePath => Path.Combine(AppPaths.CacheDirectory, "mod-hashes.json");

    public static string GetSha1(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                return "";
            }

            lock (Gate)
            {
                if (Entries.TryGetValue(path, out var cached) &&
                    cached.Length == info.Length && cached.Ticks == info.LastWriteTimeUtc.Ticks)
                {
                    return cached.Sha1;
                }
            }

            using var stream = File.OpenRead(path);
            var sha1 = Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
            lock (Gate)
            {
                Entries[path] = new Entry(info.Length, info.LastWriteTimeUtc.Ticks, sha1);
                _dirty = true;
            }

            return sha1;
        }
        catch
        {
            return "";
        }
    }

    public static void SaveIfDirty()
    {
        lock (Gate)
        {
            if (!_dirty)
            {
                return;
            }

            _dirty = false;
            try
            {
                AppPaths.EnsureCreated();
                File.WriteAllText(CachePath, JsonSerializer.Serialize(Entries));
            }
            catch
            {
                // 缓存写不进去不影响功能。
            }
        }
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(CachePath))
            {
                return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
            }

            var data = JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(CachePath));
            return data == null
                ? new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, Entry>(data, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        }
    }
}
