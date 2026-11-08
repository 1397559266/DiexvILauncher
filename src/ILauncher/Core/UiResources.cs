using System.Windows;
using System.Windows.Media;

namespace ILauncher.Core;

public static class UiResources
{
    public static Geometry? Geometry(string key)
        => Application.Current?.TryFindResource(key) as Geometry;

    public static string? Text(string key)
        => Application.Current?.TryFindResource(key) as string;
}
