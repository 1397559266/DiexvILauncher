using System.Windows.Media;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Localization;
using ILauncher.ViewModels;

namespace ILauncher.Models;

public enum InstanceSection
{
    Overview,
    Mods,
    ResourcePacks,
    Shaders,
    Saves,
    Screenshots,
    Servers,
    Settings
}

public sealed class NavItem : ObservableObject
{
    private bool _isSelected;

    public required string Title { get; init; }
    public required InstanceSection Section { get; init; }
    public Geometry? IconData { get; init; }
    public bool IsMinor { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

public enum LaunchStepState
{
    Pending,
    Active,
    Done,
    Failed
}

public sealed class LaunchStep : ObservableObject
{
    private LaunchStepState _state;

    public required string Title
    {
        get => LanguageService.Translate(_title);
        init => _title = value;
    }

    private string _title = "";
    public int Index { get; init; }
    public bool IsLast { get; init; }

    public LaunchStepState State
    {
        get => _state;
        set
        {
            if (SetProperty(ref _state, value))
            {
                OnPropertyChanged(nameof(IsPending));
                OnPropertyChanged(nameof(IsActive));
                OnPropertyChanged(nameof(IsDone));
                OnPropertyChanged(nameof(IsFailed));
                OnPropertyChanged(nameof(Brush));
                OnPropertyChanged(nameof(Glyph));
                OnPropertyChanged(nameof(GlyphBrush));
                OnPropertyChanged(nameof(StateText));
                OnPropertyChanged(nameof(ConnectorBrush));
            }
        }
    }

    public bool IsPending => State == LaunchStepState.Pending;
    public bool IsActive => State == LaunchStepState.Active;
    public bool IsDone => State == LaunchStepState.Done;
    public bool IsFailed => State == LaunchStepState.Failed;

    public string Glyph => State switch
    {
        LaunchStepState.Done => "✓",
        LaunchStepState.Failed => "!",
        _ => (Index + 1).ToString()
    };

    public string StateText => State switch
    {
        LaunchStepState.Done => LanguageService.Translate("完成"),
        LaunchStepState.Active => LanguageService.Translate("进行中"),
        LaunchStepState.Failed => LanguageService.Translate("失败"),
        _ => LanguageService.Translate("等待")
    };

    public Brush Brush => State switch
    {
        LaunchStepState.Done => new SolidColorBrush(Color.FromRgb(0x16, 0xA3, 0x4A)),
        LaunchStepState.Active => new SolidColorBrush(Color.FromRgb(0x13, 0x70, 0xF3)),
        LaunchStepState.Failed => new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)),
        _ => new SolidColorBrush(Color.FromRgb(0xDD, 0xE6, 0xF2))
    };

    public Brush GlyphBrush => State == LaunchStepState.Pending
        ? new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8))
        : Brushes.White;

    public Brush ConnectorBrush => State == LaunchStepState.Pending
        ? new SolidColorBrush(Color.FromRgb(0xE6, 0xEE, 0xF8))
        : new SolidColorBrush(Color.FromRgb(0xBF, 0xDB, 0xF7));
}

public sealed class FilterItem : ObservableObject
{
    private bool _isSelected;
    private int _count;

    public required string Title { get; init; }
    public required string Key { get; init; }
    public Geometry? DotGeometry { get; init; }
    public Brush? DotBrush { get; init; }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    public int Count
    {
        get => _count;
        set
        {
            if (SetProperty(ref _count, value))
            {
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }
    }

    public string DisplayTitle => $"{Title} {Count}";
}

public sealed class ToastModel
{
    public required string Message { get; init; }
    public bool IsError { get; init; }
}

public sealed class FolderEntry
{
    public required string Name { get; init; }
    public required string Path { get; init; }
    public required string Detail { get; init; }
    public byte[]? ImageData { get; init; }
    public string? ImagePath { get; init; }
    public Geometry? IconData { get; init; }
    public bool IsDirectory { get; init; }
    public DateTime Modified { get; init; }
}
