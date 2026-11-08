namespace ILauncher.Models;

/// <summary>Result of reading extra prerequisites from the selected mod jar.</summary>
public sealed record DependencyLoadResult(
    IReadOnlyList<ModDependencyItem> Items,
    bool Success,
    string? Error);
