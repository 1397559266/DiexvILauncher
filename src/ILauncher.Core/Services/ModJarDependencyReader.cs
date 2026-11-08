using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ILauncher.Core.Services;

public sealed record JarDependency(string ModId, bool Mandatory, string VersionRange);

public sealed record JarDependencyReadResult(
    bool Success,
    IReadOnlyList<JarDependency> Dependencies,
    string? Error);

/// <summary>
/// Reads declared mod dependencies from a mod jar. Modrinth versions sometimes
/// have an empty dependencies array even when the mod metadata declares several
/// prerequisites (notably Forge/NeoForge mods.toml).
/// </summary>
public static class ModJarDependencyReader
{
    private static readonly HashSet<string> BuiltInDependencies = new(StringComparer.OrdinalIgnoreCase)
    {
        "minecraft", "forge", "neoforge", "fabricloader", "fabric-loader", "fabric",
        "quilt_loader", "quilt-loader", "java", "javafml", "lowcodefml"
    };

    private static readonly string[] MetadataEntries =
    {
        "META-INF/mods.toml",
        "META-INF/neoforge.mods.toml",
        "fabric.mod.json",
        "quilt.mod.json"
    };

    public static IReadOnlyList<JarDependency> Read(string jarPath)
    {
        try
        {
            using var archive = ZipFile.OpenRead(jarPath);
            return ReadEntries(archive.Entries);
        }
        catch
        {
            return Array.Empty<JarDependency>();
        }
    }

    /// <summary>
    /// Reads only the ZIP central directory and the metadata entry using HTTP
    /// range requests. This avoids downloading a whole 10-100 MB mod jar just
    /// to show its prerequisites.
    /// </summary>
    public static async Task<JarDependencyReadResult> ReadFromUrlAsync(string url, long fileSize,
        CancellationToken cancellationToken)
    {
        if (fileSize <= 0)
        {
            return new JarDependencyReadResult(false, Array.Empty<JarDependency>(), "文件大小未知");
        }

        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var tailLength = (int)Math.Min(131072, fileSize);
            var tail = await ReadRangeAsync(client, url, fileSize - tailLength, fileSize - 1, cancellationToken);
            var eocd = FindEndOfCentralDirectory(tail);
            if (eocd < 0)
            {
                return new JarDependencyReadResult(false, Array.Empty<JarDependency>(), "不是有效的 JAR 文件");
            }

            var totalEntries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10, 2));
            var centralSize = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 12, 4));
            var centralOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16, 4));
            if (totalEntries == 0 || centralSize == 0 || centralSize > 16 * 1024 * 1024)
            {
                return new JarDependencyReadResult(false, Array.Empty<JarDependency>(), "JAR 中央目录无效");
            }

            var central = await ReadRangeAsync(client, url, centralOffset,
                (long)centralOffset + centralSize - 1, cancellationToken);
            var result = new List<JarDependency>();
            var cursor = 0;
            for (var index = 0; index < totalEntries && cursor + 46 <= central.Length; index++)
            {
                if (BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor, 4)) != 0x02014b50)
                {
                    break;
                }

                var method = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 10, 2));
                var compressedSize = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor + 20, 4));
                var nameLength = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 28, 2));
                var extraLength = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 30, 2));
                var commentLength = BinaryPrimitives.ReadUInt16LittleEndian(central.AsSpan(cursor + 32, 2));
                var localOffset = BinaryPrimitives.ReadUInt32LittleEndian(central.AsSpan(cursor + 42, 4));
                var nameStart = cursor + 46;
                if (nameStart + nameLength > central.Length)
                {
                    break;
                }

                var name = Encoding.UTF8.GetString(central.AsSpan(nameStart, nameLength));
                cursor = nameStart + nameLength + extraLength + commentLength;
                if (!MetadataEntries.Contains(name, StringComparer.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (compressedSize > 2 * 1024 * 1024)
                {
                    continue;
                }

                var header = await ReadRangeAsync(client, url, localOffset,
                    (long)localOffset + 30 + nameLength + 4096 + compressedSize, cancellationToken);
                if (header.Length < 30 ||
                    BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(0, 4)) != 0x04034b50)
                {
                    continue;
                }

                var localNameLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(26, 2));
                var localExtraLength = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(28, 2));
                var dataStart = 30 + localNameLength + localExtraLength;
                var dataLength = (int)Math.Min(compressedSize, (uint)Math.Max(0, header.Length - dataStart));
                if (dataStart + dataLength > header.Length)
                {
                    continue;
                }

                var data = header.AsSpan(dataStart, dataLength).ToArray();
                using var input = new MemoryStream(data);
                using Stream decoded = method == 8
                    ? new DeflateStream(input, CompressionMode.Decompress)
                    : input;
                using var reader = new StreamReader(decoded, Encoding.UTF8);
                var text = await reader.ReadToEndAsync(cancellationToken);
                ParseText(name, text, result);
            }

            return new JarDependencyReadResult(true, Filter(result), null);
        }
        catch (OperationCanceledException)
        {
            return new JarDependencyReadResult(false, Array.Empty<JarDependency>(), "读取超时");
        }
        catch (Exception ex)
        {
            return new JarDependencyReadResult(false, Array.Empty<JarDependency>(), ex.Message);
        }
    }

    private static IReadOnlyList<JarDependency> ReadEntries(IReadOnlyList<ZipArchiveEntry> entries)
    {
        var result = new List<JarDependency>();
        foreach (var name in MetadataEntries)
        {
            var entry = entries.FirstOrDefault(item =>
                item.FullName.Equals(name, StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Length > 4 * 1024 * 1024)
            {
                continue;
            }

            using var reader = new StreamReader(entry.Open(), Encoding.UTF8);
            ParseText(name, reader.ReadToEnd(), result);
        }

        return Filter(result);
    }

    internal static IReadOnlyList<JarDependency> ParseMetadataText(IReadOnlyDictionary<string, string> entries)
    {
        var result = new List<JarDependency>();
        foreach (var pair in entries)
        {
            ParseText(pair.Key, pair.Value, result);
        }

        return Filter(result);
    }

    private static void ParseText(string name, string text, List<JarDependency> result)
    {
        if (name.EndsWith("mods.toml", StringComparison.OrdinalIgnoreCase))
        {
            foreach (Match section in Regex.Matches(text,
                         @"\[\[dependencies\.[^\]]+\]\](?<body>.*?)(?=\[\[|\z)",
                         RegexOptions.Singleline | RegexOptions.IgnoreCase))
            {
                var body = section.Groups["body"].Value;
                var modId = MatchValue(body, "modId");
                if (string.IsNullOrWhiteSpace(modId))
                {
                    continue;
                }

                var mandatory = !Regex.IsMatch(body, @"mandatory\s*=\s*false", RegexOptions.IgnoreCase);
                result.Add(new JarDependency(modId, mandatory, MatchValue(body, "versionRange") ?? ""));
            }

            return;
        }

        using var document = JsonDocument.Parse(text);
        if (name.EndsWith("fabric.mod.json", StringComparison.OrdinalIgnoreCase))
        {
            AddObjectDependencies(document.RootElement, "depends", true, result);
            AddObjectDependencies(document.RootElement, "recommends", false, result);
            return;
        }

        if (name.EndsWith("quilt.mod.json", StringComparison.OrdinalIgnoreCase) &&
            document.RootElement.TryGetProperty("quilt_loader", out var loader))
        {
            AddObjectDependencies(loader, "depends", true, result);
        }
    }

    private static void AddObjectDependencies(JsonElement root, string propertyName, bool mandatory,
        List<JarDependency> result)
    {
        if (!root.TryGetProperty(propertyName, out var depends) || depends.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (var property in depends.EnumerateObject())
        {
            result.Add(new JarDependency(property.Name, mandatory, property.Value.ToString()));
        }
    }

    private static IReadOnlyList<JarDependency> Filter(List<JarDependency> dependencies)
        => dependencies
            .Where(item => !BuiltInDependencies.Contains(item.ModId))
            .GroupBy(item => item.ModId, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(item => item.Mandatory).First())
            .ToList();

    private static int FindEndOfCentralDirectory(byte[] data)
    {
        for (var index = data.Length - 22; index >= 0; index--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(index, 4)) == 0x06054b50)
            {
                return index;
            }
        }

        return -1;
    }

    private static async Task<byte[]> ReadRangeAsync(HttpClient client, string url, long start, long end,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Range = new RangeHeaderValue(start, end);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static string? MatchValue(string body, string key)
    {
        var match = Regex.Match(body, key + @"\s*=\s*""(?<value>[^""]*)""", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }
}
