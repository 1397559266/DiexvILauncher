using ILauncher.Core;
using ILauncher.Localization;

namespace ILauncher.ViewModels;

public sealed class JavaOption : ObservableObject
{
    private bool _isSelected;

    public required string Path { get; init; }
    public required string Label
    {
        get => LanguageService.Translate(_label);
        init => _label = value;
    }

    public required string Detail
    {
        get => LanguageService.Translate(_detail);
        init => _detail = value;
    }

    private string _label = "";
    private string _detail = "";
    public int Major { get; init; }
    public bool IsAuto { get; init; }

    public override string ToString() => Label;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}
