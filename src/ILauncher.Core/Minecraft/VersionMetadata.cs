using System.Text.Json;
using System.Text.Json.Serialization;

namespace ILauncher.Core.Minecraft;

public sealed class VersionMetadata
{
    public string Id { get; set; } = "";
    public string Type { get; set; } = "release";
    public string? InheritsFrom { get; set; }
    public string MainClass { get; set; } = "";
    public string? Assets { get; set; }
    public string? AssetIndexId { get; set; }
    public string? AssetIndexUrl { get; set; }
    public int JavaMajor { get; set; }
    public string? LegacyArguments { get; set; }
    public string? ClientVersion { get; set; }
    public DownloadInfo? Client { get; set; }
    public LoggingInfo? Logging { get; set; }
    public List<Library> Libraries { get; } = new();
    public List<ArgumentEntry> JvmArguments { get; } = new();
    public List<ArgumentEntry> GameArguments { get; } = new();

    public VersionMetadata Clone()
    {
        var copy = new VersionMetadata
        {
            Id = Id,
            Type = Type,
            InheritsFrom = InheritsFrom,
            MainClass = MainClass,
            Assets = Assets,
            AssetIndexId = AssetIndexId,
            AssetIndexUrl = AssetIndexUrl,
            JavaMajor = JavaMajor,
            LegacyArguments = LegacyArguments,
            ClientVersion = ClientVersion,
            Client = Client,
            Logging = Logging
        };
        copy.Libraries.AddRange(Libraries);
        copy.JvmArguments.AddRange(JvmArguments);
        copy.GameArguments.AddRange(GameArguments);
        return copy;
    }

    public static VersionMetadata Parse(string jsonPath) => VersionJson.Parse(jsonPath);
}

public sealed class DownloadInfo
{
    public string? Path { get; set; }
    public string? Url { get; set; }
    public string? Sha1 { get; set; }
    public long Size { get; set; }
}

public sealed class LoggingInfo
{
    public string? Argument { get; set; }
    public string? FileId { get; set; }
    public string? FileUrl { get; set; }
    public string? FileSha1 { get; set; }
}

public sealed class Library
{
    public string Name { get; set; } = "";
    public DownloadInfo? Artifact { get; set; }
    public Dictionary<string, DownloadInfo> Classifiers { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> Natives { get; } = new(StringComparer.OrdinalIgnoreCase);
    public List<Rule> Rules { get; } = new();
    public List<string> ExtractExclude { get; } = new();

    public bool HasRules => Rules.Count > 0;

    public bool IsNative
    {
        get
        {
            var parts = Name.Split(':');
            if (parts.Length >= 4 && parts[3].StartsWith("natives-", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            return Natives.Count > 0;
        }
    }

    /// <summary>Relative path inside the libraries folder for the classpath artifact.</summary>
    public string? RelativeArtifactPath
    {
        get
        {
            if (Artifact?.Path is { Length: > 0 } path)
            {
                return path;
            }

            if (IsNative)
            {
                return null;
            }

            return MavenPath(Name);
        }
    }

    public static string? MavenPath(string name)
    {
        // group:artifact:version[:classifier][@extension]
        var at = name.IndexOf('@');
        var extension = "jar";
        if (at >= 0)
        {
            extension = name[(at + 1)..];
            name = name[..at];
        }

        var parts = name.Split(':');
        if (parts.Length < 3)
        {
            return null;
        }

        var group = parts[0].Replace('.', '/');
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length > 3 ? "-" + parts[3] : "";
        return $"{group}/{artifact}/{version}/{artifact}-{version}{classifier}.{extension}";
    }
}

public sealed class Rule
{
    public string Action { get; set; } = "allow";
    public RuleOs? Os { get; set; }
    public Dictionary<string, bool> Features { get; } = new(StringComparer.OrdinalIgnoreCase);

    public bool Matches(IReadOnlyDictionary<string, bool> features)
    {
        if (Os != null)
        {
            if (!string.IsNullOrEmpty(Os.Name) &&
                !string.Equals(Os.Name, RuleContext.OsName, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(Os.Arch) &&
                !string.Equals(Os.Arch, RuleContext.OsArch, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (!string.IsNullOrEmpty(Os.Version) &&
                !RuleContext.OsVersionMatches(Os.Version))
            {
                return false;
            }
        }

        foreach (var (key, expected) in Features)
        {
            var actual = features.TryGetValue(key, out var value) && value;
            if (actual != expected)
            {
                return false;
            }
        }

        return true;
    }
}

public sealed class RuleOs
{
    public string? Name { get; set; }
    public string? Version { get; set; }
    public string? Arch { get; set; }
}

public sealed class ArgumentEntry
{
    public List<Rule> Rules { get; } = new();
    public List<string> Values { get; } = new();

    public bool IsAllowed(IReadOnlyDictionary<string, bool> features)
    {
        if (Rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in Rules)
        {
            if (rule.Matches(features))
            {
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }
}

public static class VersionJson
{
    public static VersionMetadata Parse(string jsonPath)
    {
        using var stream = File.OpenRead(jsonPath);
        using var document = JsonDocument.Parse(stream, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });

        var root = document.RootElement;
        var metadata = new VersionMetadata
        {
            Id = GetString(root, "id") ?? Path.GetFileNameWithoutExtension(jsonPath),
            Type = GetString(root, "type") ?? "release",
            InheritsFrom = GetString(root, "inheritsFrom"),
            MainClass = GetString(root, "mainClass") ?? "",
            Assets = GetString(root, "assets"),
            ClientVersion = GetString(root, "clientVersion"),
            LegacyArguments = GetString(root, "minecraftArguments")
        };

        if (root.TryGetProperty("javaVersion", out var javaVersion))
        {
            metadata.JavaMajor = GetInt(javaVersion, "majorVersion") ?? 0;
        }

        if (root.TryGetProperty("assetIndex", out var assetIndex))
        {
            metadata.AssetIndexId = GetString(assetIndex, "id");
            metadata.AssetIndexUrl = GetString(assetIndex, "url");
        }

        if (root.TryGetProperty("downloads", out var downloads) &&
            downloads.TryGetProperty("client", out var client))
        {
            metadata.Client = ReadDownload(client);
        }

        if (root.TryGetProperty("logging", out var logging) &&
            logging.TryGetProperty("client", out var loggingClient))
        {
            var info = new LoggingInfo
            {
                Argument = GetString(loggingClient, "argument")
            };
            if (loggingClient.TryGetProperty("file", out var file))
            {
                info.FileId = GetString(file, "id");
                info.FileUrl = GetString(file, "url");
                info.FileSha1 = GetString(file, "sha1");
            }

            metadata.Logging = info;
        }

        if (root.TryGetProperty("libraries", out var libraries) &&
            libraries.ValueKind == JsonValueKind.Array)
        {
            foreach (var element in libraries.EnumerateArray())
            {
                metadata.Libraries.Add(ReadLibrary(element));
            }
        }

        if (root.TryGetProperty("arguments", out var arguments))
        {
            if (arguments.TryGetProperty("jvm", out var jvm))
            {
                ReadArguments(jvm, metadata.JvmArguments);
            }

            if (arguments.TryGetProperty("game", out var game))
            {
                ReadArguments(game, metadata.GameArguments);
            }
        }

        return metadata;
    }

    private static void ReadArguments(JsonElement array, List<ArgumentEntry> target)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind == JsonValueKind.String)
            {
                var entry = new ArgumentEntry();
                entry.Values.Add(element.GetString()!);
                target.Add(entry);
            }
            else if (element.ValueKind == JsonValueKind.Object)
            {
                var entry = new ArgumentEntry();
                if (element.TryGetProperty("rules", out var rules))
                {
                    ReadRules(rules, entry.Rules);
                }

                if (element.TryGetProperty("value", out var value))
                {
                    ReadStringOrArray(value, entry.Values);
                }

                target.Add(entry);
            }
        }
    }

    private static void ReadStringOrArray(JsonElement value, List<string> target)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                target.Add(value.GetString()!);
                break;
            case JsonValueKind.Array:
                foreach (var item in value.EnumerateArray())
                {
                    ReadStringOrArray(item, target);
                }

                break;
        }
    }

    private static Library ReadLibrary(JsonElement element)
    {
        var library = new Library
        {
            Name = GetString(element, "name") ?? ""
        };

        if (element.TryGetProperty("downloads", out var downloads))
        {
            if (downloads.TryGetProperty("artifact", out var artifact))
            {
                library.Artifact = ReadDownload(artifact);
            }

            if (downloads.TryGetProperty("classifiers", out var classifiers))
            {
                foreach (var classifier in classifiers.EnumerateObject())
                {
                    library.Classifiers[classifier.Name] = ReadDownload(classifier.Value);
                }
            }
        }

        if (element.TryGetProperty("natives", out var natives) && natives.ValueKind == JsonValueKind.Object)
        {
            foreach (var native in natives.EnumerateObject())
            {
                if (native.Value.ValueKind == JsonValueKind.String)
                {
                    library.Natives[native.Name] = native.Value.GetString()!;
                }
            }
        }

        if (element.TryGetProperty("rules", out var rules))
        {
            ReadRules(rules, library.Rules);
        }

        if (element.TryGetProperty("extract", out var extract) &&
            extract.TryGetProperty("exclude", out var exclude) &&
            exclude.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in exclude.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    library.ExtractExclude.Add(item.GetString()!);
                }
            }
        }

        return library;
    }

    private static void ReadRules(JsonElement array, List<Rule> target)
    {
        if (array.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var element in array.EnumerateArray())
        {
            var rule = new Rule
            {
                Action = GetString(element, "action") ?? "allow"
            };

            if (element.TryGetProperty("os", out var os))
            {
                rule.Os = new RuleOs
                {
                    Name = GetString(os, "name"),
                    Version = GetString(os, "version"),
                    Arch = GetString(os, "arch")
                };
            }

            if (element.TryGetProperty("features", out var features) &&
                features.ValueKind == JsonValueKind.Object)
            {
                foreach (var feature in features.EnumerateObject())
                {
                    if (feature.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        rule.Features[feature.Name] = feature.Value.GetBoolean();
                    }
                }
            }

            target.Add(rule);
        }
    }

    private static DownloadInfo ReadDownload(JsonElement element)
        => new()
        {
            Path = GetString(element, "path"),
            Url = GetString(element, "url"),
            Sha1 = GetString(element, "sha1"),
            Size = GetLong(element, "size") ?? 0
        };

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var result) ? result : null;

    private static long? GetLong(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.TryGetInt64(out var result) ? result : null;
}

public static class RuleContext
{
    public static string OsName { get; } =
        OperatingSystem.IsWindows() ? "windows" :
        OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static string OsArch { get; } =
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture switch
        {
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "x86",
            _ => "x86_64"
        };

    public static bool OsVersionMatches(string pattern)
    {
        try
        {
            return System.Text.RegularExpressions.Regex.IsMatch(Environment.OSVersion.Version.ToString(), pattern);
        }
        catch
        {
            return true;
        }
    }
}
