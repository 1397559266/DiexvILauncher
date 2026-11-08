namespace ILauncher.Models;

/// <summary>A required or optional dependency shown in the mod install dialog.</summary>
public sealed class ModDependencyItem
{
    public required string ProjectId { get; init; }
    public string? VersionId { get; init; }
    public required string Title { get; init; }
    public required string DependencyType { get; init; }
    public bool Installed { get; init; }
    public int Depth { get; init; }

    public bool Required => string.Equals(DependencyType, "required", StringComparison.OrdinalIgnoreCase);
    public bool Optional => string.Equals(DependencyType, "optional", StringComparison.OrdinalIgnoreCase);
    public bool Missing => Required && !Installed;

    public string TypeText => Required ? "必需" : Optional ? "可选" : DependencyType;
    public string StatusText => Installed ? "已安装" : Required ? "缺失" : "未安装";
    public string DisplayTitle => Depth <= 0 ? Title : $"{new string('　', Depth)}└ {Title}";
}
