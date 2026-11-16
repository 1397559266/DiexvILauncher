using System.IO.Compression;
using System.Text;

namespace ILauncher.Core.Services;

/// <summary>一个带 Java Agent 入口的 jar（manifest 里声明了 Premain-Class / Agent-Class）。</summary>
public sealed record JarAgentInfo(
    string FilePath,
    string FileName,
    string? PremainClass,
    string? AgentClass,
    string? LauncherAgentClass)
{
    public bool HasAgent => !string.IsNullOrWhiteSpace(PremainClass) ||
                            !string.IsNullOrWhiteSpace(AgentClass) ||
                            !string.IsNullOrWhiteSpace(LauncherAgentClass);

    /// <summary>建议加入的 JVM 参数；Path 里含空格时用引号包起来。</summary>
    public string SuggestedArgument =>
        $"-javaagent:\"{FilePath}\"";
}

/// <summary>
/// 读取 jar 的 META-INF/MANIFEST.MF，识别是否是 javaagent（Premain-Class）。
/// 只做只读扫描，不加载任何类。
/// </summary>
public static class JarAgentScanner
{
    public static IReadOnlyList<string> ListJars(string modsDirectory)
    {
        try
        {
            return Directory.Exists(modsDirectory)
                ? Directory.EnumerateFiles(modsDirectory, "*.jar", SearchOption.TopDirectoryOnly)
                    .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase)
                    .ToList()
                : [];
        }
        catch
        {
            return [];
        }
    }

    public static JarAgentInfo Inspect(string jarPath)
    {
        var fileName = Path.GetFileName(jarPath);
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            var manifest = ReadManifest(archive);
            if (manifest == null)
            {
                return new JarAgentInfo(jarPath, fileName, null, null, null);
            }

            manifest.TryGetValue("Premain-Class", out var premain);
            manifest.TryGetValue("Agent-Class", out var agent);
            manifest.TryGetValue("Launcher-Agent-Class", out var launcherAgent);
            return new JarAgentInfo(jarPath, fileName, premain, agent, launcherAgent);
        }
        catch
        {
            // 损坏的 jar 当作没有 agent 处理，不影响其它模组。
            return new JarAgentInfo(jarPath, fileName, null, null, null);
        }
    }

    public static IReadOnlyList<JarAgentInfo> Scan(IEnumerable<string> jarPaths,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        var paths = jarPaths.ToList();
        var result = new List<JarAgentInfo>();
        for (var index = 0; index < paths.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var info = Inspect(paths[index]);
            if (info.HasAgent)
            {
                result.Add(info);
            }

            if (index % 10 == 0)
            {
                progress?.Report(index * 100 / Math.Max(1, paths.Count));
            }
        }

        progress?.Report(100);
        return result;
    }

    private static Dictionary<string, string>? ReadManifest(ZipArchive archive)
    {
        var entry = archive.GetEntry("META-INF/MANIFEST.MF");
        if (entry == null)
        {
            return null;
        }

        using var stream = entry.Open();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var manifest = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        string? currentKey = null;
        var value = new StringBuilder();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                Flush(manifest, currentKey, value);
                currentKey = null;
                continue;
            }

            if (line[0] == ' ' && currentKey != null)
            {
                // 折行：续行以一个空格开头。
                value.Append(line[1..]);
                continue;
            }

            Flush(manifest, currentKey, value);
            var separator = line.IndexOf(':');
            if (separator <= 0)
            {
                currentKey = null;
                continue;
            }

            currentKey = line[..separator].Trim();
            value.Clear();
            value.Append(line[(separator + 1)..].Trim());
        }

        Flush(manifest, currentKey, value);
        return manifest;
    }

    private static void Flush(Dictionary<string, string> manifest, string? key, StringBuilder value)
    {
        if (!string.IsNullOrWhiteSpace(key) && value.Length > 0)
        {
            manifest[key] = value.ToString().Trim();
        }

        value.Clear();
    }
}
