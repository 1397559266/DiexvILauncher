using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ILauncher.Core.Services;

public sealed record McmodInfo(string Name, string Url);

/// <summary>
/// Resolves a Modrinth project title to an MC百科 (mcmod.cn) entry.
/// MC百科 has no public JSON API, so this uses its public search page and
/// keeps a small local cache to avoid repeated requests.
/// </summary>
public sealed class McmodService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly Dictionary<string, string> KnownAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["钠"] = "Sodium",
        ["锂"] = "Lithium",
        ["磷"] = "Phosphor",
        ["铷"] = "Rubidium",
        ["铁氧体磁芯"] = "FerriteCore",
        ["实体渲染机制优化"] = "Entity Culling",
        ["苹果皮"] = "AppleSkin",
        ["小地图"] = "Xaero's Minimap",
        ["玉"] = "Jade",
        ["机械动力"] = "Create",
        ["农夫乐事"] = "Farmer's Delight",
        ["匠魂"] = "Tinkers' Construct",
        ["暮色森林"] = "Twilight Forest",
        ["旅行地图"] = "JourneyMap"
    };
    private static readonly Regex ResultLink = new(
        @"<a\s+[^>]*href=""(?<url>(?:(?:https?:)?//www\.mcmod\.cn)?/class/(?<id>\d+)\.html)""[^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Tag = new("<.*?>", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly object _gate = new();
    private readonly Dictionary<string, McmodInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _aliases = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _requests = new(1, 1);
    private bool _loaded;

    private static string CachePath => Path.Combine(AppPaths.DataDirectory, "mcmod-cache-v3.json");
    private static string AliasCachePath => Path.Combine(AppPaths.DataDirectory, "mcmod-alias-cache-v1.json");

    public async Task<McmodInfo?> LookupAsync(string title, CancellationToken cancellationToken = default)
    {
        var key = Normalize(title);
        if (key.Length < 2)
        {
            return null;
        }

        lock (_gate)
        {
            EnsureLoaded();
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        await _requests.WaitAsync(cancellationToken);
        try
        {
            // MC百科 throttles bursts of searches; keep this deliberately
            // sequential and leave a little breathing room between requests.
            await Task.Delay(120, cancellationToken);
            var url = "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(title.Trim()) + "&filter=1&mold=";
            string? html = null;
            for (var attempt = 0; attempt < 2 && html == null; attempt++)
            {
                try
                {
                    html = await Http.GetStringAsync(url, cancellationToken);
                }
                catch when (attempt == 0)
                {
                    await Task.Delay(400, cancellationToken);
                }
            }

            var result = html == null ? null : Parse(html, title);
            if (result == null && html != null)
            {
                // Some searches intermittently render an empty/anti-bot page.
                // Retry once before recording a negative result.
                await Task.Delay(350, cancellationToken);
                try
                {
                    html = await Http.GetStringAsync(url, cancellationToken);
                    result = Parse(html, title);
                }
                catch
                {
                    return null;
                }
            }

            if (html != null)
            {
                lock (_gate)
                {
                    _cache[key] = result;
                    Save();
                }
            }

            return result;
        }
        catch
        {
            // Do not cache transport failures: the next search should retry.
            return null;
        }
        finally
        {
            _requests.Release();
        }
    }

    public async Task<string?> FindEnglishAliasAsync(string chineseQuery, CancellationToken cancellationToken = default)
    {
        var key = Normalize(chineseQuery);
        if (key.Length < 1)
        {
            return null;
        }

        if (KnownAliases.TryGetValue(chineseQuery.Trim(), out var known))
        {
            return known;
        }

        lock (_gate)
        {
            EnsureLoaded();
            if (_aliases.TryGetValue(key, out var cached))
            {
                return cached;
            }

            // Reuse the metadata cache as a reverse Chinese-name index when
            // possible, so an already-resolved mod can be searched in Chinese
            // without another MC百科 request.
            foreach (var pair in _cache)
            {
                if (pair.Value?.Name.Contains(chineseQuery.Trim(), StringComparison.OrdinalIgnoreCase) == true)
                {
                    _aliases[key] = pair.Key;
                    return pair.Key;
                }
            }
        }

        await _requests.WaitAsync(cancellationToken);
        try
        {
            await Task.Delay(120, cancellationToken);
            var url = "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(chineseQuery.Trim()) + "&filter=1&mold=";
            string? html = null;
            for (var attempt = 0; attempt < 2 && html == null; attempt++)
            {
                try
                {
                    html = await Http.GetStringAsync(url, cancellationToken);
                }
                catch when (attempt == 0)
                {
                    await Task.Delay(400, cancellationToken);
                }
            }

            var alias = html == null ? null : ParseEnglishAlias(html, chineseQuery);
            if (html != null)
            {
                lock (_gate)
                {
                    _aliases[key] = alias;
                    SaveAliases();
                }
            }

            return alias;
        }
        catch
        {
            return null;
        }
        finally
        {
            _requests.Release();
        }
    }

    private static string? ParseEnglishAlias(string html, string query)
    {
        var queryKey = Normalize(query);
        string? best = null;
        var bestScore = 0;
        var bestLength = int.MaxValue;

        foreach (Match match in ResultLink.Matches(html))
        {
            var title = CleanTitle(match.Groups["title"].Value);
            if (!HasCjk(title))
            {
                continue;
            }

            var parenthetical = Regex.Match(title, @"[\(（]([^\)）]+)[\)）]\s*$");
            if (!parenthetical.Success)
            {
                continue;
            }

            var english = WebUtility.HtmlDecode(Tag.Replace(parenthetical.Groups[1].Value, " ")).Trim(' ', '_', '-');
            if (english.Length < 2 || !Regex.IsMatch(english, "[A-Za-z]") || HasCjk(english))
            {
                continue;
            }

            var chinese = title[..parenthetical.Index].Trim();
            var chineseKey = Normalize(chinese);
            var score = chineseKey == queryKey
                ? 100
                : chineseKey.Contains(queryKey, StringComparison.Ordinal)
                    ? 80
                    : queryKey.Contains(chineseKey, StringComparison.Ordinal)
                        ? 60
                        : 0;
            if (score == 0)
            {
                continue;
            }

            var length = chinese.Length;
            if (score > bestScore || (score == bestScore && length < bestLength))
            {
                best = english;
                bestScore = score;
                bestLength = length;
            }
        }

        return best;
    }

    private static McmodInfo? Parse(string html, string projectTitle)
    {        var projectKey = Normalize(projectTitle);
        McmodInfo? best = null;
        var bestScore = 0;
        var bestLength = int.MaxValue;

        foreach (Match match in ResultLink.Matches(html))
        {
            var title = CleanTitle(match.Groups["title"].Value);

            var candidateKey = Normalize(title);
            var parenthetical = Regex.Match(title, @"[\(（]([^\)）]+)[\)）]\s*$");
            var parentheticalKey = parenthetical.Success ? Normalize(parenthetical.Groups[1].Value) : "";
            var looksLikeAddon = Regex.IsMatch(title, "非官方|附属|兼容|修复|扩展|前置|移植|重置|增强|支持|插件");
            if (looksLikeAddon && candidateKey != projectKey && parentheticalKey != projectKey)
            {
                continue;
            }

            var score = projectKey.Length == 0
                ? 0
                : candidateKey == projectKey
                    ? 100
                    : parentheticalKey == projectKey
                        ? 95
                        : candidateKey.StartsWith(projectKey, StringComparison.Ordinal)
                            ? 75
                            : candidateKey.Contains(projectKey, StringComparison.Ordinal)
                                ? 65
                                : projectKey.Contains(candidateKey, StringComparison.Ordinal)
                                    ? 55
                                    : 0;
            if (score == 0)
            {
                continue;
            }

            var length = title.Length;
            if (score > bestScore || (score == bestScore && length < bestLength))
            {
                bestScore = score;
                bestLength = length;
                best = new McmodInfo(ShortName(title), $"https://www.mcmod.cn/class/{match.Groups["id"].Value}.html");
            }
        }

        return best;
    }

    private static string CleanTitle(string value)
    {
        var text = WebUtility.HtmlDecode(Tag.Replace(value, " "));
        text = Regex.Replace(text, @"\s+", " ").Trim();
        text = Regex.Replace(text, @"^\[[^\]]+\]\s*", "");
        return text;
    }

    private static string ShortName(string title)
    {
        var name = Regex.Replace(title, @"\s*[\(（][^\)）]*[\)）]\s*$", "").Trim();
        return HasCjk(name) ? name : title;
    }

    private static bool HasCjk(string value)
        => value.Any(ch => ch is >= '\u3400' and <= '\u9fff');

    private static string Normalize(string value)
    {
        var builder = new System.Text.StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(char.ToLowerInvariant(ch));
            }
        }

        return builder.ToString();
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
                var cached = JsonSerializer.Deserialize<Dictionary<string, McmodInfo?>>(File.ReadAllText(CachePath));
                if (cached != null)
                {
                    foreach (var pair in cached)
                    {
                        _cache[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch
        {
            // Ignore a damaged cache; it is rebuilt on demand.
        }

        try
        {
            if (File.Exists(AliasCachePath))
            {
                var aliases = JsonSerializer.Deserialize<Dictionary<string, string?>>(File.ReadAllText(AliasCachePath));
                if (aliases != null)
                {
                    foreach (var pair in aliases)
                    {
                        _aliases[pair.Key] = pair.Value;
                    }
                }
            }
        }
        catch
        {
            // Ignore a damaged alias cache.
        }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(CachePath, json);
        }
        catch
        {
            // A read-only data directory only skips cache persistence.
        }
    }

    private void SaveAliases()
    {
        try
        {
            var json = JsonSerializer.Serialize(_aliases, new JsonSerializerOptions { WriteIndented = false });
            File.WriteAllText(AliasCachePath, json);
        }
        catch
        {
            // A read-only data directory only skips cache persistence.
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) ILauncher/1.1");
        return client;
    }
}
