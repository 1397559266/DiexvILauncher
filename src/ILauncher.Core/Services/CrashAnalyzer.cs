using System.Text.RegularExpressions;

namespace ILauncher.Core.Services;

public static class CrashAnalyzer
{
    public static List<string> Analyze(IEnumerable<string> lines)
    {
        var text = string.Join('\n', lines);
        var tips = new List<string>();

        if (text.Contains("java.lang.OutOfMemoryError", StringComparison.OrdinalIgnoreCase))
        {
            tips.Add("内存不足：请在版本设置里提高最大内存，或减少同时启用的模组数量。");
        }

        if (text.Contains("UnsupportedClassVersionError", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("has been compiled by a more recent version of the Java Runtime", StringComparison.OrdinalIgnoreCase))
        {
            tips.Add("Java 版本过低：请为该版本切换到 Java 17 或 21。");
        }

        if (text.Contains("Duplicate mods", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("Found duplicate mods", StringComparison.OrdinalIgnoreCase))
        {
            tips.Add("发现重复模组：同一个模组放了多个版本，请只保留一个。");
        }

        if (text.Contains("Mixin apply failed", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("MixinApplyError", StringComparison.OrdinalIgnoreCase))
        {
            tips.Add("Mixin 注入失败：通常是模组与当前 Minecraft/加载器版本不兼容，请检查最近安装的模组。");
        }

        if (text.Contains("NoSuchMethodError", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("NoClassDefFoundError", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("ClassNotFoundException", StringComparison.OrdinalIgnoreCase))
        {
            tips.Add("缺少类或方法：可能缺少前置模组，或模组版本与游戏/加载器不匹配。");
        }

        if (text.Contains("OptiFine", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("Forge", StringComparison.OrdinalIgnoreCase) &&
            text.Contains("crash", StringComparison.OrdinalIgnoreCase))
        {
            tips.Add("OptiFine 与 Forge 可能不兼容：请确认两者版本匹配，或改用 Embeddium/Oculus 等替代方案。");
        }

        if (text.Contains("The game crashed whilst", StringComparison.OrdinalIgnoreCase) ||
            text.Contains("A fatal error has been detected", StringComparison.OrdinalIgnoreCase))
        {
            tips.Add("游戏发生了致命崩溃：请查看 crash-reports 目录里的最新报告和日志中的第一条 Exception。");
        }

        var missing = Regex.Match(text, @"Missing or unsupported mandatory dependencies?:?\s*(?<deps>[^\r\n]+)",
            RegexOptions.IgnoreCase);
        if (missing.Success)
        {
            tips.Add($"缺少前置模组：{missing.Groups["deps"].Value.Trim()}");
        }

        if (tips.Count == 0 && text.Contains("ERROR", StringComparison.Ordinal))
        {
            tips.Add("日志中存在 ERROR，请从第一条 ERROR / Exception 开始排查；常见原因是模组不兼容或缺少前置。");
        }

        return tips.Distinct().Take(4).ToList();
    }
}
