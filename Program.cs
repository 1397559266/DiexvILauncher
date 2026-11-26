using System.Reflection;
using System.Windows;
using System.IO;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.ViewModels;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        var settings = new SettingsService();
        var java = new JavaService(settings);
        var vm = new DownloadViewModel(
            settings,
            new MojangGameService(),
            new FabricService(),
            new QuiltService(),
            new ModrinthService(),
            new ModLoaderInstallerService(java, settings),
            new ModpackService(new MojangGameService(), new FabricService(), new QuiltService(), new ModLoaderInstallerService(new JavaService(settings), settings)),
            (message, error) => Console.WriteLine($"  toast: {message}"),
            () => null,
            () => Array.Empty<GameInstance>(),
            () => Path.GetTempPath(),
            () => Task.CompletedTask);

        var project = new ModrinthProject("testproj", "test-slug", "Test Project", "desc", null, 1, "mod",
            Array.Empty<string>());
        vm.SearchResults.Add(project);

        var changes = 0;
        project.PropertyChanged += (_, _) => changes++;

        var method = typeof(DownloadViewModel).GetMethod("ToggleFavorite", BindingFlags.NonPublic | BindingFlags.Instance)!;
        method.Invoke(vm, new object?[] { project });
        Console.WriteLine($"IsFavorite after toggle : {project.IsFavorite} (expect True)");
        Console.WriteLine($"glyph                  : {project.FavoriteGlyph} (expect ★)");
        Console.WriteLine($"property changes       : {changes} (expect > 0)");
        Console.WriteLine($"row still in collection: {vm.SearchResults.Contains(project)}");

        method.Invoke(vm, new object?[] { project });
        Console.WriteLine($"after second toggle    : {project.IsFavorite} (expect False), glyph {project.FavoriteGlyph}");
    }
}
