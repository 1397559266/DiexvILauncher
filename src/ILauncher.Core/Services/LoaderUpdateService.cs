using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ILauncher.Core.Models;

namespace ILauncher.Core.Services;

/// <summary>可选的加载器版本。Stable 仅 Fabric 有意义（false 表示测试版）。</summary>
public sealed record LoaderVersionOption(string Version, bool Stable)
{
    public string Label => Stable ? Version : $"{Version}（测试版）";

    public override string ToString() => Label;
}

/// <summary>
/// 把模组加载器升级到指定版本：先让各自的安装器把新版本装成临时版本，
/// 再把它的 JSON 改写成当前实例的 id 覆盖过去，这样模组、存档、配置都留在原实例里。
/// </summary>
public sealed class LoaderUpdateService
{
    private readonly ModLoaderInstallerService _installers;
    private readonly FabricService _fabric = new();
    private readonly QuiltService _quilt = new();

    public LoaderUpdateService(JavaService java, SettingsService settings)
    {
        _installers = new ModLoaderInstallerService(java, settings);
    }

    public static bool CanUpdate(GameInstance instance) =>
        instance.Loader is "Forge" or "NeoForge" or "Fabric" or "Quilt";

    public async Task<IReadOnlyList<LoaderVersionOption>> GetVersionsAsync(GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        var minecraft = instance.MinecraftVersion;
        switch (instance.Loader)
        {
            case "Forge":
                return SortVersions(await _installers.GetForgeVersionsAsync(minecraft, cancellationToken))
                    .Select(version => new LoaderVersionOption(version, true)).ToList();
            case "NeoForge":
                return SortVersions(await _installers.GetNeoForgeVersionsAsync(minecraft, cancellationToken))
                    .Select(version => new LoaderVersionOption(version, true)).ToList();
            case "Fabric":
                return SortVersions((await _fabric.GetLoadersAsync(minecraft, cancellationToken))
                        .Select(loader => loader.Version))
                    .Select(version => new LoaderVersionOption(version, true)).ToList();
            case "Quilt":
                return SortVersions((await _quilt.GetLoadersAsync(minecraft, cancellationToken))
                        .Select(loader => loader.Version))
                    .Select(version => new LoaderVersionOption(version, true)).ToList();
            default:
                return [];
        }
    }

    /// <summary>按数字段倒序排（47.4.29 &gt; 47.4.5），保证最新版排在最前。</summary>
    private static IEnumerable<string> SortVersions(IEnumerable<string> versions) =>
        versions.OrderByDescending(version => version, Comparer<string>.Create(CompareVersions));

    private static int CompareVersions(string? left, string? right)
    {
        var a = (left ?? "").Split('.', '-', '+');
        var b = (right ?? "").Split('.', '-', '+');
        for (var index = 0; index < Math.Max(a.Length, b.Length); index++)
        {
            var leftPart = index < a.Length ? a[index] : "0";
            var rightPart = index < b.Length ? b[index] : "0";
            var leftNumber = int.TryParse(leftPart, out var leftValue);
            var rightNumber = int.TryParse(rightPart, out var rightValue);
            int result;
            if (leftNumber && rightNumber)
            {
                result = leftValue.CompareTo(rightValue);
            }
            else
            {
                result = string.Compare(leftPart, rightPart, StringComparison.OrdinalIgnoreCase);
            }

            if (result != 0)
            {
                return result;
            }
        }

        return 0;
    }

    public async Task UpdateAsync(GameInstance instance, string loaderVersion,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        if (!CanUpdate(instance))
        {
            throw new InvalidOperationException("这是原版实例，没有可更新的模组加载器。");
        }

        var minecraft = instance.MinecraftVersion;
        string temporaryId = instance.Loader switch
        {
            "Forge" => await _installers.InstallForgeVersionAsync(minecraft, loaderVersion, instance.RootDirectory,
                           progress, cancellationToken)
                       ?? throw new InvalidOperationException("Forge 安装器没有返回版本 id。"),
            "NeoForge" => await _installers.InstallNeoForgeVersionAsync(minecraft, loaderVersion,
                              instance.RootDirectory, progress, cancellationToken)
                          ?? throw new InvalidOperationException("NeoForge 安装器没有返回版本 id。"),
            "Fabric" => await _fabric.InstallAsync(minecraft, loaderVersion, instance.RootDirectory, progress,
                cancellationToken),
            "Quilt" => await _quilt.InstallAsync(minecraft, loaderVersion, instance.RootDirectory, progress,
                cancellationToken),
            _ => throw new InvalidOperationException("不支持的加载器类型。")
        };

        AdoptVersion(instance, temporaryId);
    }

    /// <summary>把临时版本目录里的 JSON/其它文件改成当前实例的 id 后覆盖过去。</summary>
    private static void AdoptVersion(GameInstance instance, string temporaryId)
    {
        var tempDirectory = Path.Combine(instance.RootDirectory, "versions", temporaryId);
        var tempJson = Path.Combine(tempDirectory, temporaryId + ".json");
        if (!File.Exists(tempJson))
        {
            throw new IOException($"安装器没有生成 {temporaryId}.json，无法应用到当前实例。");
        }

        var text = File.ReadAllText(tempJson);
        var node = JsonNode.Parse(text) as JsonObject
                   ?? throw new IOException("加载器 JSON 解析失败。");
        node["id"] = instance.Id;

        // 如果安装器生成了同名 jar，也一并搬过来并改写 jar 字段。
        var tempJar = Path.Combine(tempDirectory, temporaryId + ".jar");
        if (File.Exists(tempJar))
        {
            node["jar"] = instance.Id;
        }

        Directory.CreateDirectory(instance.Directory);
        var targetJson = Path.Combine(instance.Directory, instance.Id + ".json");
        if (File.Exists(targetJson))
        {
            File.Copy(targetJson, targetJson + ".bak", overwrite: true);
        }

        File.WriteAllText(targetJson, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        foreach (var file in Directory.EnumerateFiles(tempDirectory))
        {
            var name = Path.GetFileName(file);
            if (string.Equals(name, temporaryId + ".json", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var targetName = name.StartsWith(temporaryId, StringComparison.OrdinalIgnoreCase)
                ? instance.Id + name[temporaryId.Length..]
                : name;
            File.Copy(file, Path.Combine(instance.Directory, targetName), overwrite: true);
        }

        // 临时版本目录已经没用了（依赖库在 libraries 下共享）。
        try
        {
            Directory.Delete(tempDirectory, true);
        }
        catch
        {
            // 删不掉就留着，不影响启动。
        }
    }
}
