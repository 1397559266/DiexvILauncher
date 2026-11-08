using ILauncher.Core;

namespace ILauncher.ViewModels;

public sealed class JavaOption : ObservableObject
{
    private bool _isSelected;

    public required string Path { get; init; }
    public required string Label { get; init; }
    public required string Detail { get; init; }
    public int Major { get; init; }
    public bool IsAuto { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
