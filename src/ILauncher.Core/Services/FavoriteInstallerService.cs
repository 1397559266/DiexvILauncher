using System.IO;
using ILauncher.Core.Models;

namespace ILauncher.Core.Services;

/// <summary>把收藏的资源下载/安装到指定实例。</summary>
public sealed class FavoriteInstallerService
{
    private readonly ModrinthService _modrinth = new();
    private readonly CurseForgeService _curseForge = new();

    public static string TargetDirectory(FavoriteEntry favorite, GameInstance instance) => favorite.Kind switch
    {
        FavoriteKinds.Mod => instance.ModsDirectory,
        FavoriteKinds.Shader => Path.Combine(instance.GameDirectory, "shaderpacks"),
        FavoriteKinds.ResourcePack => Path.Combine(instance.GameDirectory, "resourcepacks"),
        FavoriteKinds.Modpack => Path.Combine(instance.RootDirectory, "modpacks"),
        _ => instance.ModsDirectory
    };

    public async Task<string> InstallAsync(FavoriteEntry favorite, GameInstance instance,
        IProgress<string>? status, CancellationToken cancellationToken = default)
    {
        var directory = TargetDirectory(favorite, instance);
        Directory.CreateDirectory(directory);
        status?.Report($"准备下载 {favorite.Title}…");

        // 1) 收藏时保存过文件 —— 直接复制。
        if (favorite.HasLocalFile)
        {
            var fileName = Dependencies.SafeFileName(favorite.FileName ?? Path.GetFileName(favorite.LocalFile!));
            var path = Path.Combine(directory, fileName);
            File.Copy(favorite.LocalFile!, path, overwrite: true);
            status?.Report($"已从本地收藏复制到 {Path.GetFileName(path)}");
            return path;
        }

        // 2) Modrinth：按项目 id + 游戏版本/加载器取最新文件。
        if (string.Equals(favorite.Source, "Modrinth", StringComparison.OrdinalIgnoreCase))
        {
            var projectId = favorite.ProjectId;
            var versions = await _modrinth.GetVersionsAsync(projectId, instance.MinecraftVersion,
                NormalizeLoader(instance.Loader), cancellationToken);
            var version = versions.FirstOrDefault(v => v.File != null);
            if (version?.File == null)
            {
                throw new InvalidOperationException($"没有找到适用于 {instance.MinecraftVersion} 的文件。");
            }

            status?.Report($"下载 {version.File.FileName}…");
            var path = Path.Combine(directory, Dependencies.SafeFileName(version.File.FileName));
            await DownloadHelper.DownloadFileAsync(version.File.Url, path, null, cancellationToken,
                version.File.Sha1);
            return path;
        }

        // 3) CurseForge：cf:<modId> 取最新兼容文件。
        if (string.Equals(favorite.Source, "CurseForge", StringComparison.OrdinalIgnoreCase))
        {
            var raw = favorite.ProjectId.StartsWith("cf:", StringComparison.OrdinalIgnoreCase)
                ? favorite.ProjectId[3..]
                : favorite.ProjectId;
            if (!long.TryParse(raw, out var modId))
            {
                throw new InvalidOperationException("收藏的 CurseForge 项目 id 无效。");
            }

            var files = await _curseForge.GetFilesAsync(modId, instance.MinecraftVersion,
                NormalizeLoader(instance.Loader), cancellationToken);
            var file = files.FirstOrDefault(f => f.File != null)?.File;
            if (file == null)
            {
                throw new InvalidOperationException($"没有找到适用于 {instance.MinecraftVersion} 的文件。");
            }

            status?.Report($"下载 {file.FileName}…");
            var path = Path.Combine(directory, Dependencies.SafeFileName(file.FileName));
            await DownloadHelper.DownloadFileAsync(file.Url, path, null, cancellationToken, file.Sha1);
            return path;
        }

        throw new InvalidOperationException("无法识别的收藏来源。");
    }

    private static string? NormalizeLoader(string loader) => loader switch
    {
        "Fabric" => "fabric",
        "Forge" => "forge",
        "NeoForge" => "neoforge",
        "Quilt" => "quilt",
        _ => null
    };
}

internal static class Dependencies
{
    public static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        return string.IsNullOrWhiteSpace(cleaned) ? "favorite.bin" : cleaned;
    }
}
