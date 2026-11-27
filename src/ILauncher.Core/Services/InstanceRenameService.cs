using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using ILauncher.Core.Models;

namespace ILauncher.Core.Services;

/// <summary>把版本目录整体改名：versions\&lt;旧名&gt; → versions\&lt;新名&gt;，并同步 JSON 里的 id。</summary>
public static class InstanceRenameService
{
    public static void Validate(string newId, GameInstance instance)
    {
        if (string.IsNullOrWhiteSpace(newId))
        {
            throw new InvalidOperationException("版本名不能为空。");
        }

        newId = newId.Trim();
        if (newId.Length > 64)
        {
            throw new InvalidOperationException("版本名太长（最多 64 个字符）。");
        }

        if (newId.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException("版本名里不能包含 \\ / : * ? \" < > | 这些字符。");
        }

        if (string.Equals(newId, instance.Id, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("新名字和现在的一样。");
        }

        var target = Path.Combine(instance.RootDirectory, "versions", newId);
        if (Directory.Exists(target))
        {
            throw new InvalidOperationException($"已经有一个叫「{newId}」的版本了。");
        }
    }

    /// <summary>执行改名，返回新的版本 id。</summary>
    public static string Rename(GameInstance instance, string newId)
    {
        newId = newId.Trim();
        Validate(newId, instance);

        var oldDirectory = instance.Directory;
        var newDirectory = Path.Combine(instance.RootDirectory, "versions", newId);
        Directory.Move(oldDirectory, newDirectory);

        var oldJson = Path.Combine(newDirectory, instance.Id + ".json");
        var newJson = Path.Combine(newDirectory, newId + ".json");
        if (!File.Exists(oldJson))
        {
            throw new IOException($"找不到版本文件 {instance.Id}.json。");
        }

        var node = JsonNode.Parse(File.ReadAllText(oldJson)) as JsonObject
                   ?? throw new IOException("版本 JSON 解析失败。");
        node["id"] = newId;
        File.WriteAllText(newJson, node.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        if (!string.Equals(oldJson, newJson, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(oldJson);
        }

        // 安装器有时会生成与版本同名的 jar，也一并改名。
        var oldJar = Path.Combine(newDirectory, instance.Id + ".jar");
        if (File.Exists(oldJar))
        {
            File.Move(oldJar, Path.Combine(newDirectory, newId + ".jar"), overwrite: true);
        }

        return newId;
    }
}
