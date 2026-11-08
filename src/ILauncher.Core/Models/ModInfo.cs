namespace ILauncher.Core.Models;

public sealed class ModInfo
{
    public required string FilePath { get; set; }

    public required string FileName { get; set; }

    public bool Enabled { get; set; }

    public string Name { get; set; } = "";

    public string Version { get; set; } = "";

    public string ModId { get; set; } = "";

    public string Description { get; set; } = "";

    public string Loader { get; set; } = "";

    public long Size { get; set; }

    public DateTime LastWriteTime { get; set; }

    /// <summary>Optional PNG icon embedded in the mod jar.</summary>
    public byte[]? IconData { get; set; }

    public string DisplayName => string.IsNullOrWhiteSpace(Name)
        ? Path.GetFileNameWithoutExtension(FileName).Replace(".jar", "", StringComparison.OrdinalIgnoreCase)
        : Name;

    public string SizeText => Size switch
    {
        >= 1024 * 1024 => $"{Size / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{Size / 1024d:0} KB",
        _ => $"{Size} B"
    };
}
