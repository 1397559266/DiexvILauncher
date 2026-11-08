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
        var url = $"https://maven.minecraftforge.net/net/minecraftforge/forge/{coordinate}/forge-{coordinate}-installer.jar";
        return InstallViaInstallerAsync("Forge", minecraftVersion, forgeVersion, url, rootDirectory, progress, cancellationToken);
    }

    public Task<string?> InstallNeoForgeVersionAsync(string minecraftVersion, string neoForgeVersion, string rootDirectory,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        var url = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{neoForgeVersion}/neoforge-{neoForgeVersion}-installer.jar";
        return InstallViaInstallerAsync("NeoForge", minecraftVersion, neoForgeVersion, url, rootDirectory, progress, cancellationToken);
    }

    public async Task<string?> InstallNeoForgeAsync(string minecraftVersion, string rootDirectory,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        var version = await GetNeoForgeVersionAsync(minecraftVersion, cancellationToken)
                      ?? throw new InvalidOperationException($"没有找到适用于 {minecraftVersion} 的 NeoForge 版本。");
        var url = $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{version}/neoforge-{version}-installer.jar";
        return await InstallViaInstallerAsync("NeoForge", minecraftVersion, version, url, rootDirectory, progress, cancellationToken);
    }

    private async Task<string?> InstallViaInstallerAsync(string loader, string minecraftVersion, string loaderVersion,
        string installerUrl, string rootDirectory, Action<DownloadProgress>? progress, CancellationToken cancellationToken)
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
        await DownloadHelper.DownloadFileAsync(installerUrl, installerPath, null, cancellationToken);

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

    private static async Task<string?> GetNeoForgeVersionAsync(string minecraftVersion, CancellationToken cancellationToken)
    {
        var match = Regex.Match(minecraftVersion, @"^1\.(\d+)(?:\.(\d+))?");
        if (!match.Success)
        {
            return null;
        }

        var minor = int.Parse(match.Groups[1].Value);
        var patch = match.Groups[2].Success ? int.Parse(match.Groups[2].Value) : 0;
        var prefix = minor == 20 && patch <= 1
            ? "47.1."
            : $"{minor}.{patch}.";
        try
        {
            var xml = await DownloadHelper.GetStringAsync(
                "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml", cancellationToken);
            var document = XDocument.Parse(xml);
            return document.Descendants("version")
                .Select(element => element.Value)
                .Where(value => value.StartsWith(prefix, StringComparison.Ordinal))
                .Where(value => !value.Contains("installer", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(value => value, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
        }
        catch
        {
            return null;
        }
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
