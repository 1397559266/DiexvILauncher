namespace ILauncher.Models;

/// <summary>A required or optional dependency shown in the mod install dialog.</summary>
public sealed class ModDependencyItem
{
    public required string ProjectId { get; init; }
    public string? VersionId { get; init; }
    public string? Slug { get; init; }
    public required string Title { get; init; }
    public required string DependencyType { get; init; }
    public bool Installed { get; init; }
    public bool Resolved { get; init; } = true;
    public int Depth { get; init; }

    public bool Required => string.Equals(DependencyType, "required", StringComparison.OrdinalIgnoreCase);
    public bool Optional => string.Equals(DependencyType, "optional", StringComparison.OrdinalIgnoreCase);
    public bool Missing => Required && Resolved && !Installed;
    public bool UnresolvedRequired => Required && !Resolved;
    public bool CanInstall => Resolved && !Installed;

    public string TypeText => Required ? "必需" : Optional ? "可选" : DependencyType;
    public string StatusText => !Resolved ? "无法解析" : Installed ? "已安装" : Required ? "缺失" : "未安装";
    public string DisplayTitle => Depth <= 0 ? Title : $"{new string('　', Depth)}└ {Title}";
    public string CurseForgeUrl =>
        $"https://www.curseforge.com/minecraft/search?search={Uri.EscapeDataString(Title)}&class=mc-mods";
    public string ModrinthUrl => Resolved && !string.IsNullOrWhiteSpace(Slug)
        ? $"https://modrinth.com/mod/{Uri.EscapeDataString(Slug)}"
        : $"https://modrinth.com/mods?q={Uri.EscapeDataString(Title)}";
}
