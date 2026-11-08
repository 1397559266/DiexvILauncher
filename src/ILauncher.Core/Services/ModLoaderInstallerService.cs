using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace ILauncher.Core.Services;

/// <summary>Runs the official Forge / NeoForge installer jars in headless client mode.</summary>
public sealed class ModLoaderInstallerService
{
    private readonly JavaService _java;
    private readonly SettingsService _settings;

    public ModLoaderInstallerService(JavaService java, SettingsService settings)
    {
        _java = java;
        _settings = settings;
    }

    public async Task<string?> InstallForgeAsync(string minecraftVersion, string rootDirectory,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        var version = await GetForgeVersionAsync(minecraftVersion, cancellationToken)
                      ?? throw new InvalidOperationException($"没有找到适用于 {minecraftVersion} 的 Forge 版本。");
        return await InstallForgeVersionAsync(minecraftVersion, version, rootDirectory, progress, cancellationToken);
    }

    public Task<string?> InstallForgeVersionAsync(string minecraftVersion, string forgeVersion, string rootDirectory,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        var coordinate = $"{minecraftVersion}-{forgeVersion}";
        var official = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinate}/forge-{coordinate}-installer.jar";
        var mirror = $"{DownloadSourceConfig.MirrorBase}/maven/net/minecraftforge/forge/{coordinate}/forge-{coordinate}-installer.jar";
        return InstallViaInstallerAsync("Forge", minecraftVersion, forgeVersion, mirror, official,
            rootDirectory, progress, cancellationToken);
    }

    public Task<string?> InstallNeoForgeVersionAsync(string minecraftVersion, string neoForgeVersion, string rootDirectory,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        string official;
        string mirror;
        if (IsLegacyNeoForge(minecraftVersion))
        {
            var coordinate = $"{minecraftVersion}-{neoForgeVersion}";
            official = $"https://maven.neoforged.net/releases/net/neoforged/forge/{coordinate}/forge-{coordinate}-installer.jar";
            mirror = $"{DownloadSourceConfig.MirrorBase}/maven/net/neoforged/forge/{coordinate}/forge-{coordinate}-installer.jar";
        }
        else
        {
            official = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{neoForgeVersion}/neoforge-{neoForgeVersion}-installer.jar";
            mirror = $"{DownloadSourceConfig.MirrorBase}/maven/net/neoforged/neoforge/{neoForgeVersion}/neoforge-{neoForgeVersion}-installer.jar";
        }

        return InstallViaInstallerAsync("NeoForge", minecraftVersion, neoForgeVersion, mirror, official,
            rootDirectory, progress, cancellationToken);
    }

    public async Task<string?> InstallNeoForgeAsync(string minecraftVersion, string rootDirectory,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        var version = await GetNeoForgeVersionAsync(minecraftVersion, cancellationToken)
                      ?? throw new InvalidOperationException($"没有找到适用于 {minecraftVersion} 的 NeoForge 版本。");
        return await InstallNeoForgeVersionAsync(minecraftVersion, version, rootDirectory, progress, cancellationToken);
    }

    private async Task<string?> InstallViaInstallerAsync(string loader, string minecraftVersion, string loaderVersion,
        string installerUrl, string fallbackInstallerUrl, string rootDirectory,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        if (!File.Exists(Path.Combine(rootDirectory, "versions", minecraftVersion, minecraftVersion + ".json")))
        {
            throw new InvalidOperationException($"请先安装原版 {minecraftVersion}，{loader} 需要以它为基础。");
        }

        var requiredJava = GuessJavaMajor(minecraftVersion);
        var java = await _java.ResolveAsync(requiredJava, _settings.Settings.JavaPath, cancellationToken)
                   ?? throw new InvalidOperationException($"没有找到 Java {requiredJava}+。");
        var installerDirectory = Path.Combine(AppPaths.CacheDirectory, "installers");
        Directory.CreateDirectory(installerDirectory);
        var installerPath = Path.Combine(installerDirectory,
            $"{loader.ToLowerInvariant()}-{minecraftVersion}-{loaderVersion}-installer.jar");

        // The official Forge/NeoForge installers refuse to run unless the target
        // looks like a launcher directory. A fresh .minecraft often lacks this file.
        var profilesPath = Path.Combine(rootDirectory, "launcher_profiles.json");
        if (!File.Exists(profilesPath))
        {
            Directory.CreateDirectory(rootDirectory);
            await File.WriteAllTextAsync(profilesPath,
                "{\"profiles\":{},\"settings\":{},\"version\":3}", cancellationToken);
        }

        progress?.Invoke(new DownloadProgress { Phase = loader, Message = $"下载 {loader} {loaderVersion} 安装器…", Percent = 3 });
        try
        {
            await DownloadHelper.DownloadFileAsync(installerUrl, installerPath, null, cancellationToken);
        }
        catch when (!string.Equals(installerUrl, fallbackInstallerUrl, StringComparison.OrdinalIgnoreCase))
        {
            await DownloadHelper.DownloadFileAsync(fallbackInstallerUrl, installerPath, null, cancellationToken);
        }

        progress?.Invoke(new DownloadProgress
        {
            Phase = loader,
            Message = $"运行 {loader} 官方安装器（Java {java.Major}）…",
            Percent = -1
        });

        var startInfo = new ProcessStartInfo(java.Path)
        {
            WorkingDirectory = rootDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(rootDirectory);

        using var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        var output = new StringBuilder();
        if (!process.Start())
        {
            throw new InvalidOperationException("无法启动安装器进程。");
        }

        void OnLine(string? line)
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(line);
                if (output.Length > 12000)
                {
                    output.Remove(0, output.Length - 9000);
                }
            }

            progress?.Invoke(new DownloadProgress
            {
                Phase = loader,
                Message = line.Length > 160 ? line[..160] : line,
                Percent = -1
            });
        }

        process.OutputDataReceived += (_, e) => OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => OnLine(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch
            {
                // ignored
            }

            throw;
        }

        if (process.ExitCode != 0)
        {
            var tail = output.ToString().Split('\n').TakeLast(8);
            throw new InvalidOperationException($"{loader} 安装器退出码 {process.ExitCode}：{string.Join(" ", tail).Trim()}");
        }

        var candidates = Directory.EnumerateDirectories(Path.Combine(rootDirectory, "versions"))
            .Where(d => Path.GetFileName(d).Contains(loader, StringComparison.OrdinalIgnoreCase) ||
                        Path.GetFileName(d).Contains("forge", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(Directory.GetLastWriteTime)
            .ToList();
        var result = candidates.FirstOrDefault(d => File.Exists(Path.Combine(d, Path.GetFileName(d) + ".json")));
        var id = result == null ? null : Path.GetFileName(result);
        progress?.Invoke(new DownloadProgress { Phase = "完成", Message = $"已安装 {id ?? loader}", Percent = 100 });
        return id;
    }

    public async Task<List<string>> GetForgeVersionsAsync(string minecraftVersion, CancellationToken cancellationToken = default)
    {
        var versions = new List<string>();
        try
        {
            var json = await DownloadHelper.GetStringAsync(
                "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("promos", out var promos))
            {
                foreach (var key in new[] { $"{minecraftVersion}-recommended", $"{minecraftVersion}-latest" })
                {
                    if (promos.TryGetProperty(key, out var value) &&
                        value.GetString() is { Length: > 0 } version)
                    {
                        versions.Add(version);
                    }
                }
            }
        }
        catch
        {
            // The maven metadata below is the fallback.
        }

        try
        {
            var xml = await GetMavenMetadataAsync("net/minecraftforge/forge", "https://maven.minecraftforge.net", cancellationToken);
            var document = XDocument.Parse(xml);
            var prefix = minecraftVersion + "-";
            versions.AddRange(document.Descendants("version")
                .Select(element => element.Value)
                .Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
                .Select(value => value[prefix.Length..])
                .Where(value => !value.Contains("installer", StringComparison.OrdinalIgnoreCase)));
        }
        catch
        {
            // Keep promotion versions if maven is unavailable.
        }

        return versions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<List<string>> GetNeoForgeVersionsAsync(string minecraftVersion, CancellationToken cancellationToken = default)
    {
        var legacy = IsLegacyNeoForge(minecraftVersion);
        var artifact = legacy ? "net/neoforged/forge" : "net/neoforged/neoforge";
        var prefix = legacy ? minecraftVersion + "-" : GetModernNeoForgePrefix(minecraftVersion);
        if (string.IsNullOrEmpty(prefix))
        {
            return new List<string>();
        }

        try
        {
            var xml = await GetMavenMetadataAsync(artifact, "https://maven.neoforged.net/releases", cancellationToken);
            var document = XDocument.Parse(xml);
            return document.Descendants("version")
                .Select(element => element.Value)
                .Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
                .Select(value => legacy ? value[prefix.Length..] : value)
                .Where(value => !value.Contains("installer", StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static string GetModernNeoForgePrefix(string minecraftVersion)
    {
        var match = Regex.Match(minecraftVersion, @"^(?:1\.)?(\d+)\.(\d+)");
        return match.Success ? $"{match.Groups[1].Value}.{match.Groups[2].Value}." : "";
    }

    private static bool IsLegacyNeoForge(string minecraftVersion)
        => string.Equals(minecraftVersion, "1.20.1", StringComparison.OrdinalIgnoreCase);

    private static async Task<string> GetMavenMetadataAsync(string artifactPath, string officialBase,
        CancellationToken cancellationToken)
    {
        try
        {
            return await DownloadHelper.GetStringAsync(
                $"{DownloadSourceConfig.MirrorBase}/maven/{artifactPath}/maven-metadata.xml", cancellationToken);
        }
        catch
        {
            return await DownloadHelper.GetStringAsync(
                $"{officialBase}/{artifactPath}/maven-metadata.xml", cancellationToken);
        }
    }

    private static async Task<string?> GetForgeVersionAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        try
        {
            var json = await DownloadHelper.GetStringAsync(
                "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", cancellationToken);
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.TryGetProperty("promos", out var promos))
            {
                foreach (var key in new[] { $"{minecraftVersion}-recommended", $"{minecraftVersion}-latest" })
                {
                    if (promos.TryGetProperty(key, out var value) &&
                        value.GetString() is { Length: > 0 } version)
                    {
                        return version;
                    }
                }
            }
        }
        catch
        {
            // Fall back to maven metadata.
        }

        try
        {
            var xml = await DownloadHelper.GetStringAsync(
                "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", cancellationToken);
            var document = XDocument.Parse(xml);
            var prefix = minecraftVersion + "-";
            return document.Descendants("version")
                .Select(element => element.Value)
                .Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
                .Select(value => value[prefix.Length..])
                .Where(value => !value.Contains("installer", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private async Task<string?> GetNeoForgeVersionAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        var versions = await GetNeoForgeVersionsAsync(minecraftVersion, cancellationToken);
        return versions.LastOrDefault();
    }

    private static int GuessJavaMajor(string minecraftVersion)
    {
        var match = Regex.Match(minecraftVersion, @"^1\.(\d+)(?:\.(\d+))?");
        if (!match.Success)
        {
            return 17;
        }

        var minor = int.Parse(match.Groups[1].Value);
        var patch = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        if (minor > 20 || (minor == 20 && patch >= 5))
        {
            return 21;
        }

        if (minor >= 18)
        {
            return 17;
        }

        if (minor >= 17)
        {
            return 16;
        }

        return 8;
    }
}
