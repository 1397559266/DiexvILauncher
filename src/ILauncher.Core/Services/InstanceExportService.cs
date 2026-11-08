using System.IO.Compression;
using System.Text;
using ILauncher.Core.Models;

namespace ILauncher.Core.Services;

public static class InstanceExportService
{
    private static readonly string[] ExcludedDirectories =
    {
        "logs", "crash-reports", "screenshots", "natives", ".ilauncher", "cache"
    };

    public static string CreateLaunchScript(LaunchPlan plan, string destination)
    {
        var builder = new StringBuilder();
        builder.AppendLine("@echo off");
        builder.AppendLine("chcp 65001 > nul");
        builder.AppendLine($"title ILauncher - {plan.VersionId}");
        builder.AppendLine($"cd /d \"{plan.WorkingDirectory}\"");
        builder.Append('"').Append(plan.JavaPath).Append('"');
        foreach (var argument in plan.Arguments)
        {
            builder.Append(' ');
            if (argument.Contains(' ') || argument.Contains('"'))
            {
                builder.Append('"').Append(argument.Replace("\"", "\\\"")).Append('"');
            }
            else
            {
                builder.Append(argument);
            }
        }

        builder.AppendLine();
        builder.AppendLine("echo.");
        builder.AppendLine("echo 游戏已退出，按任意键关闭窗口。");
        builder.AppendLine("pause > nul");
        File.WriteAllText(destination, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return destination;
    }

    public static async Task ExportZipAsync(GameInstance instance, string destination,
        Action<DownloadProgress>? progress, CancellationToken cancellationToken = default)
    {
        var files = new List<string>();
        var root = instance.Directory;
        foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(root, file);
            var first = relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
            if (ExcludedDirectories.Contains(first, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            files.Add(relative);
        }

        await Task.Run(() =>
        {
            if (File.Exists(destination))
            {
                File.Delete(destination);
            }

            using var archive = ZipFile.Open(destination, ZipArchiveMode.Create);
            var completed = 0;
            foreach (var relative in files)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = Path.Combine(root, relative);
                var entry = archive.CreateEntry(relative.Replace('\\', '/'), CompressionLevel.Optimal);
                using var input = File.OpenRead(source);
                using var output = entry.Open();
                input.CopyTo(output);
                completed++;
                progress?.Invoke(new DownloadProgress
                {
                    Phase = "导出实例",
                    Message = $"{completed}/{files.Count} · {relative}",
                    Percent = files.Count == 0 ? 100 : completed * 100d / files.Count
                });
            }

            var info = archive.CreateEntry("ilauncher-instance.json");
            using var writer = new StreamWriter(info.Open(), new UTF8Encoding(false));
            writer.WriteLine("{");
            writer.WriteLine($"  \"id\": \"{Escape(instance.Id)}\",");
            writer.WriteLine($"  \"minecraft\": \"{Escape(instance.MinecraftVersion)}\",");
            writer.WriteLine($"  \"loader\": \"{Escape(instance.Loader)}\",");
            writer.WriteLine($"  \"loaderVersion\": \"{Escape(instance.LoaderVersion)}\",");
            writer.WriteLine($"  \"exportedAt\": \"{DateTime.Now:O}\"");
            writer.WriteLine("}");
        }, cancellationToken);
    }

    private static string Escape(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
