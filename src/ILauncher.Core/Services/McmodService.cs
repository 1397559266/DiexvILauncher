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
    private static readonly Regex ResultLink = new(
        @"<a\s+[^>]*href=""(?<url>(?:(?:https?:)?//www\.mcmod\.cn)?/class/(?<id>\d+)\.html)""[^>]*>(?<title>.*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex Tag = new("<.*?>", RegexOptions.Compiled | RegexOptions.Singleline);

    private readonly object _gate = new();
    private readonly Dictionary<string, McmodInfo?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _requests = new(3, 3);
    private bool _loaded;

    private static string CachePath => Path.Combine(AppPaths.DataDirectory, "mcmod-cache.json");

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
            var url = "https://search.mcmod.cn/s?key=" + Uri.EscapeDataString(title.Trim()) + "&filter=1&mold=";
            var html = await Http.GetStringAsync(url, cancellationToken);
            var result = Parse(html, title);
            lock (_gate)
            {
                _cache[key] = result;
                Save();
            }

            return result;
        }
        catch
        {
            // A missing/broken lookup must not break the download page.
            lock (_gate)
            {
                _cache[key] = null;
            }

            return null;
        }
        finally
        {
            _requests.Release();
        }
    }

    private static McmodInfo? Parse(string html, string projectTitle)
    {
        var projectKey = Normalize(projectTitle);
        McmodInfo? best = null;
        var bestScore = 0;
        var bestLength = int.MaxValue;

        foreach (Match match in ResultLink.Matches(html))
        {
            var title = CleanTitle(match.Groups["title"].Value);
            if (!HasCjk(title))
            {
                continue;
            }

            var candidateKey = Normalize(title);
            var score = projectKey.Length == 0
                ? 0
                : candidateKey == projectKey
                    ? 100
                    : candidateKey.StartsWith(projectKey, StringComparison.Ordinal)
                        ? 80
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
            if (!File.Exists(CachePath))
            {
                return;
            }

            var cached = JsonSerializer.Deserialize<Dictionary<string, McmodInfo?>>(File.ReadAllText(CachePath));
            if (cached == null)
            {
                return;
            }

            foreach (var pair in cached)
            {
                _cache[pair.Key] = pair.Value;
            }
        }
        catch
        {
            // Ignore a damaged cache; it is rebuilt on demand.
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

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ILauncher/1.1");
        return client;
    }
}
