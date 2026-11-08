using ILauncher.Core;

namespace ILauncher.Models;

public sealed class SkinEntry : ObservableObject
{
    private bool _isSelected;

    public required string Name { get; init; }
    public required string Path { get; init; }
    public string Model { get; init; } = "Steve";
    public bool IsBuiltIn { get; init; }
    public string SourceText => IsBuiltIn ? "内置" : "本地";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
