using System.IO.Compression;
using System.Text.Json;

namespace ILauncher.Core.Services;

/// <summary>Installs Modrinth modpacks (.mrpack) with their declared loader and files.</summary>
public sealed class ModpackService
{
    private readonly MojangGameService _mojang;
    private readonly FabricService _fabric;
    private readonly QuiltService _quilt;
    private readonly ModLoaderInstallerService _installers;

    public ModpackService(MojangGameService mojang, FabricService fabric, QuiltService quilt,
        ModLoaderInstallerService installers)
    {
        _mojang = mojang;
        _fabric = fabric;
        _quilt = quilt;
        _installers = installers;
    }

    public async Task<string> InstallAsync(string archivePath, string rootDirectory,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        var temp = Path.Combine(Path.GetTempPath(), "ilauncher-pack-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            progress?.Invoke(new DownloadProgress { Phase = "整合包", Message = "正在解压整合包…", Percent = 2 });
            ZipFile.ExtractToDirectory(archivePath, temp);

            var index = Path.Combine(temp, "modrinth.index.json");
            if (!File.Exists(index))
            {
                throw new InvalidOperationException("仅支持 Modrinth 整合包（缺少 modrinth.index.json）。");
            }

            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(index, cancellationToken));
            var root = document.RootElement;
            var name = root.TryGetProperty("name", out var nameElement) ? nameElement.GetString() ?? "整合包" : "整合包";
            var minecraftVersion = "";
            string? fabricVersion = null;
            string? quiltVersion = null;
            string? forgeVersion = null;
            string? neoForgeVersion = null;
            if (root.TryGetProperty("dependencies", out var dependencies))
            {
                minecraftVersion = GetString(dependencies, "minecraft") ?? "";
                fabricVersion = GetString(dependencies, "fabric-loader");
                quiltVersion = GetString(dependencies, "quilt-loader");
                forgeVersion = GetString(dependencies, "forge");
                neoForgeVersion = GetString(dependencies, "neoforge");
            }

            if (string.IsNullOrWhiteSpace(minecraftVersion))
            {
                throw new InvalidOperationException("整合包没有声明 Minecraft 版本。");
            }

            var baseJson = Path.Combine(rootDirectory, "versions", minecraftVersion, minecraftVersion + ".json");
            if (!File.Exists(baseJson))
            {
                progress?.Invoke(new DownloadProgress
                {
                    Phase = "基础版本",
                    Message = $"安装原版 {minecraftVersion}…",
                    Percent = 5
                });
                var versions = await _mojang.GetVersionsAsync(cancellationToken);
                var target = versions.FirstOrDefault(version => version.Id == minecraftVersion)
                             ?? throw new InvalidOperationException($"Mojang 版本列表中没有 {minecraftVersion}。");
                await _mojang.InstallAsync(target, rootDirectory, downloadAssets: true, progress, cancellationToken);
            }

            progress?.Invoke(new DownloadProgress { Phase = "加载器", Message = "安装模组加载器…", Percent = 15 });
            string instanceId;
            if (!string.IsNullOrWhiteSpace(fabricVersion))
            {
                instanceId = await _fabric.InstallAsync(minecraftVersion, fabricVersion, rootDirectory, progress, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(quiltVersion))
            {
                instanceId = await _quilt.InstallAsync(minecraftVersion, quiltVersion, rootDirectory, progress, cancellationToken);
            }
            else if (!string.IsNullOrWhiteSpace(forgeVersion))
            {
                instanceId = await _installers.InstallForgeVersionAsync(minecraftVersion, forgeVersion, rootDirectory, progress, cancellationToken)
                             ?? throw new InvalidOperationException("Forge 安装器没有返回实例。");
            }
            else if (!string.IsNullOrWhiteSpace(neoForgeVersion))
            {
                instanceId = await _installers.InstallNeoForgeVersionAsync(minecraftVersion, neoForgeVersion, rootDirectory, progress, cancellationToken)
                             ?? throw new InvalidOperationException("NeoForge 安装器没有返回实例。");
            }
            else
            {
                throw new InvalidOperationException("该整合包没有声明加载器，暂不支持无加载器整合包。");
            }

            var instanceDirectory = Path.Combine(rootDirectory, "versions", instanceId);
            foreach (var folder in new[] { "overrides", "client-overrides" })
            {
                var source = Path.Combine(temp, folder);
                if (Directory.Exists(source))
                {
                    CopyDirectory(source, instanceDirectory);
                }
            }

            CopyPackIcon(temp, instanceDirectory);

            var files = new List<(string Path, string Url)>();
            if (root.TryGetProperty("files", out var fileArray))
            {
                foreach (var file in fileArray.EnumerateArray())
                {
                    var path = GetString(file, "path");
                    if (string.IsNullOrWhiteSpace(path) ||
                        !file.TryGetProperty("downloads", out var downloads) ||
                        downloads.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    var url = downloads.EnumerateArray().Select(item => item.GetString()).FirstOrDefault(item => !string.IsNullOrWhiteSpace(item));
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        files.Add((path!, url!));
                    }
                }
            }

            var completed = 0;
            foreach (var file in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var relative = file.Path.Replace('/', Path.DirectorySeparatorChar);
                var target = Path.GetFullPath(Path.Combine(instanceDirectory, relative));
                if (!target.StartsWith(Path.GetFullPath(instanceDirectory), StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    await DownloadHelper.DownloadFileAsync(file.Url, target, null, cancellationToken);
                }
                catch
                {
                    // Keep going; report individual missing files in the game log later.
                }

                completed++;
                progress?.Invoke(new DownloadProgress
                {
                    Phase = "整合包",
                    Message = $"{name} · {completed}/{files.Count}",
                    Percent = files.Count == 0 ? 95 : 20 + completed * 75d / files.Count
                });
            }

            progress?.Invoke(new DownloadProgress { Phase = "完成", Message = $"{name} 已安装", Percent = 100 });
            return instanceId;
        }
        finally
        {
            try
            {
                if (Directory.Exists(temp))
                {
                    Directory.Delete(temp, recursive: true);
                }
            }
            catch
            {
                // ignored
            }
        }
    }

    private static void CopyPackIcon(string extractedDirectory, string instanceDirectory)
    {
        var target = Path.Combine(instanceDirectory, "icon.png");
        if (File.Exists(target))
        {
            return;
        }

        foreach (var relative in new[]
                 {
                     "icon.png",
                     "pack.png",
                     Path.Combine("overrides", "icon.png"),
                     Path.Combine("client-overrides", "icon.png")
                 })
        {
            var source = Path.Combine(extractedDirectory, relative);
            if (!File.Exists(source))
            {
                continue;
            }

            try
            {
                Directory.CreateDirectory(instanceDirectory);
                File.Copy(source, target);
                return;
            }
            catch
            {
                // A missing icon is not fatal; the loader or block icon is used.
            }
        }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
