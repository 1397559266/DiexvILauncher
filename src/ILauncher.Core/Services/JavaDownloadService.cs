using System.IO.Compression;
using System.Text.Json;

namespace ILauncher.Core.Services;

public sealed record JavaPackage(int Major, string Version, string Url, string FileName, long Size, string? Sha256);

public sealed class JavaDownloadService
{
    public async Task<JavaPackage?> GetPackageAsync(int major, CancellationToken cancellationToken = default)
    {
        var url = $"https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";
        var json = await DownloadHelper.GetStringAsync(url, cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() == 0)
        {
            return null;
        }

        var first = document.RootElement[0];
        if (!first.TryGetProperty("binary", out var binary) ||
            !binary.TryGetProperty("package", out var package) ||
            !package.TryGetProperty("link", out var link) ||
            !package.TryGetProperty("name", out var name))
        {
            return null;
        }

        var version = "";
        if (first.TryGetProperty("version", out var versionElement) &&
            versionElement.TryGetProperty("semver", out var semver))
        {
            version = semver.GetString() ?? "";
        }

        return new JavaPackage(major, version, link.GetString() ?? "", name.GetString() ?? "",
            package.TryGetProperty("size", out var size) && size.TryGetInt64(out var sizeValue) ? sizeValue : 0,
            package.TryGetProperty("checksum", out var checksum) ? checksum.GetString() : null);
    }

    public async Task<string> InstallAsync(int major, Action<DownloadProgress>? progress,
        CancellationToken cancellationToken = default)
    {
        var package = await GetPackageAsync(major, cancellationToken)
                      ?? throw new InvalidOperationException($"未能获取 Java {major} 下载地址。");
        var root = Path.Combine(AppPaths.DataDirectory, "java");
        var targetDirectory = Path.Combine(root, $"{major}-{Sanitize(package.Version)}");
        var javaExe = Directory.Exists(targetDirectory)
            ? Directory.EnumerateFiles(targetDirectory, "java.exe", SearchOption.AllDirectories).FirstOrDefault()
            : null;
        if (javaExe != null)
        {
            return javaExe;
        }

        Directory.CreateDirectory(root);
        var zipPath = Path.Combine(root, package.FileName);
        progress?.Invoke(new DownloadProgress
        {
            Phase = $"Java {major}",
            Message = $"下载 {package.FileName}（{package.Size / 1024d / 1024d:0} MB）…",
            Percent = 2
        });
        await DownloadHelper.DownloadFileAsync(package.Url, zipPath, (completed, total) =>
        {
            progress?.Invoke(new DownloadProgress
            {
                Phase = $"Java {major}",
                Message = total > 0
                    ? $"{completed / 1024d / 1024d:0.0}/{total / 1024d / 1024d:0.0} MB"
                    : $"{completed / 1024d / 1024d:0.0} MB",
                Percent = total > 0 ? 5 + completed * 80d / total : -1,
                Completed = completed,
                Total = total
            });
        }, cancellationToken, expectedSha256: package.Sha256);

        progress?.Invoke(new DownloadProgress { Phase = $"Java {major}", Message = "解压中…", Percent = 85 });
        var temp = Path.Combine(root, "extract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            ZipFile.ExtractToDirectory(zipPath, temp);
            var found = Directory.EnumerateFiles(temp, "java.exe", SearchOption.AllDirectories)
                .OrderBy(path => path.Length)
                .FirstOrDefault();
            if (found == null)
            {
                throw new InvalidOperationException("下载包中没有找到 java.exe。");
            }

            var sourceRoot = Directory.GetParent(Path.GetDirectoryName(found)!)!.FullName;
            if (Directory.Exists(targetDirectory))
            {
                Directory.Delete(targetDirectory, recursive: true);
            }

            Directory.Move(sourceRoot, targetDirectory);
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

        progress?.Invoke(new DownloadProgress { Phase = "完成", Message = $"Java {major} 已安装", Percent = 100 });
        var result = Directory.EnumerateFiles(targetDirectory, "java.exe", SearchOption.AllDirectories).FirstOrDefault();
        return result ?? throw new InvalidOperationException("Java 安装完成，但找不到 java.exe。");
    }

    private static string Sanitize(string value)
    {
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            value = value.Replace(invalid, '_');
        }

        return value;
    }
}
