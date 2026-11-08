using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO.Compression;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.Models;
using Microsoft.Win32;

namespace ILauncher.ViewModels;

public sealed class FolderBrowserViewModel : ObservableObject
{
    private readonly GameInstance _instance;
    private readonly Action<string, bool> _toast;
    private bool _isLoading;
    private string _folderPath = "";

    public FolderBrowserViewModel(GameInstance instance, InstanceSection section, Action<string, bool> toast)
    {
        _instance = instance;
        _toast = toast;
        Section = section;

        (Title, Description, FolderName, EmptyText, IconData) = section switch
        {
            InstanceSection.ResourcePacks => ("资源包", "管理游戏使用的资源包与材质包。", "resourcepacks",
                "还没有安装资源包。把资源包放进 resourcepacks 文件夹即可。", Application.Current?.TryFindResource("IconLayers") as Geometry),
            InstanceSection.Shaders => ("光影", "管理光影包，通常需要 Iris 或 OptiFine。", "shaderpacks",
                "还没有安装光影。把光影包放进 shaderpacks 文件夹即可。", Application.Current?.TryFindResource("IconSparkles") as Geometry),
            InstanceSection.Saves => ("存档", "浏览当前版本的游戏存档。", "saves",
                "还没有存档。进入游戏创建一个世界后会显示在这里。", Application.Current?.TryFindResource("IconGlobe") as Geometry),
            InstanceSection.Screenshots => ("截图", "浏览游戏内拍摄的截图。", "screenshots",
                "还没有截图。在游戏里按 F2 拍摄的截图会显示在这里。", Application.Current?.TryFindResource("IconImage") as Geometry),
            _ => ("资源", "", "", "暂无内容。", null)
        };

        RefreshCommand = new AsyncRelayCommand(LoadAsync);
        OpenFolderCommand = new RelayCommand(OpenFolder);
        OpenEntryCommand = new RelayCommand(parameter =>
        {
            if (parameter is FolderEntry entry)
            {
                OpenEntry(entry);
            }
        });
        InstallCommand = new RelayCommand(Install, () => CanInstall);
        DeleteCommand = new RelayCommand(parameter =>
        {
            if (parameter is FolderEntry entry)
            {
                DeleteEntry(entry);
            }
        });
        BackupCommand = new RelayCommand(parameter =>
        {
            if (parameter is FolderEntry entry)
            {
                Backup(entry);
            }
        }, _ => CanBackup);
        CanInstall = Section is InstanceSection.ResourcePacks or InstanceSection.Shaders;
        CanBackup = Section == InstanceSection.Saves;
    }

    public InstanceSection Section { get; }
    public string Title { get; }
    public string Description { get; }
    public string FolderName { get; }
    public string EmptyText { get; }
    public Geometry? IconData { get; }
    public bool CanInstall { get; }
    public bool CanBackup { get; }

    public ObservableCollection<FolderEntry> Entries { get; } = new();

    public string FolderPath
    {
        get => _folderPath;
        private set => SetProperty(ref _folderPath, value);
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public bool IsEmpty => Entries.Count == 0;

    public string CountText => $"{Entries.Count} 项";

    public AsyncRelayCommand RefreshCommand { get; }
    public RelayCommand OpenFolderCommand { get; }
    public RelayCommand OpenEntryCommand { get; }
    public RelayCommand InstallCommand { get; }
    public RelayCommand DeleteCommand { get; }
    public RelayCommand BackupCommand { get; }

    public async Task LoadAsync()
    {
        FolderPath = Path.Combine(_instance.GameDirectory, FolderName);
        IsLoading = true;
        try
        {
            var entries = await Task.Run(() => Scan());
            Entries.Clear();
            foreach (var entry in entries)
            {
                Entries.Add(entry);
            }

            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(CountText));
        }
        catch (Exception ex)
        {
            _toast($"读取失败：{ex.Message}", true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private List<FolderEntry> Scan()
    {
        var results = new List<FolderEntry>();
        if (!Directory.Exists(FolderPath))
        {
            return results;
        }

        if (Section == InstanceSection.Saves)
        {
            foreach (var directory in Directory.EnumerateDirectories(FolderPath))
            {
                var levelDat = Path.Combine(directory, "level.dat");
                if (!File.Exists(levelDat))
                {
                    continue;
                }

                results.Add(new FolderEntry
                {
                    Name = Path.GetFileName(directory),
                    Path = directory,
                    Detail = $"存档 · {new DirectoryInfo(directory).LastWriteTime:yyyy-MM-dd HH:mm}",
                    Modified = new DirectoryInfo(directory).LastWriteTime,
                    ImagePath = File.Exists(Path.Combine(directory, "icon.png")) ? Path.Combine(directory, "icon.png") : null,
                    IconData = Application.Current?.TryFindResource("IconGlobe") as Geometry,
                    IsDirectory = true
                });
            }
        }
        else if (Section == InstanceSection.Screenshots)
        {
            foreach (var file in Directory.EnumerateFiles(FolderPath).Where(IsImage))
            {
                var info = new FileInfo(file);
                results.Add(new FolderEntry
                {
                    Name = info.Name,
                    Path = file,
                    Detail = $"{info.LastWriteTime:yyyy-MM-dd HH:mm} · {FormatSize(info.Length)}",
                    Modified = info.LastWriteTime,
                    ImagePath = file,
                    IconData = Application.Current?.TryFindResource("IconImage") as Geometry,
                    IsDirectory = false
                });
            }
        }
        else
        {
            foreach (var file in Directory.EnumerateFiles(FolderPath))
            {
                var info = new FileInfo(file);
                results.Add(new FolderEntry
                {
                    Name = info.Name,
                    Path = file,
                    Detail = $"{FormatSize(info.Length)} · {info.LastWriteTime:yyyy-MM-dd}",
                    Modified = info.LastWriteTime,
                    ImagePath = Section == InstanceSection.ResourcePacks ? FindPackIcon(file) : null,
                    IconData = Application.Current?.TryFindResource("IconBox") as Geometry,
                    IsDirectory = false
                });
            }

            foreach (var directory in Directory.EnumerateDirectories(FolderPath))
            {
                var info = new DirectoryInfo(directory);
                results.Add(new FolderEntry
                {
                    Name = info.Name,
                    Path = directory,
                    Detail = $"文件夹 · {info.LastWriteTime:yyyy-MM-dd}",
                    Modified = info.LastWriteTime,
                    ImagePath = Section == InstanceSection.ResourcePacks ? FindPackIcon(directory) : null,
                    IconData = Application.Current?.TryFindResource("IconFolder") as Geometry,
                    IsDirectory = true
                });
            }
        }

        return results
            .OrderByDescending(e => e.SectionPriority())
            .ThenByDescending(e => e.Modified)
            .ToList();
    }

    private static bool IsImage(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".png", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
               || extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase);
    }

    private static string FormatSize(long size) => size switch
    {
        >= 1024 * 1024 => $"{size / 1024d / 1024d:0.0} MB",
        >= 1024 => $"{size / 1024d:0} KB",
        _ => $"{size} B"
    };

    private void OpenFolder()
    {
        try
        {
            Directory.CreateDirectory(FolderPath);
            Process.Start(new ProcessStartInfo(FolderPath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast($"无法打开文件夹：{ex.Message}", true);
        }
    }

    private void OpenEntry(FolderEntry entry)
    {
        try
        {
            Process.Start(new ProcessStartInfo(entry.Path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast($"无法打开：{ex.Message}", true);
        }
    }

    private void Install()
    {
        var dialog = new OpenFileDialog
        {
            Title = Section == InstanceSection.Shaders ? "选择光影包" : "选择资源包",
            Filter = "ZIP 压缩包 (*.zip)|*.zip",
            Multiselect = false
        };

        if (dialog.ShowDialog() != true)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(FolderPath);
            var target = Path.Combine(FolderPath, Path.GetFileName(dialog.FileName));
            var index = 2;
            while (File.Exists(target))
            {
                var name = Path.GetFileNameWithoutExtension(dialog.FileName);
                target = Path.Combine(FolderPath, $"{name} ({index++}).zip");
            }

            File.Copy(dialog.FileName, target);
            _toast($"已导入 {Path.GetFileName(target)}。", false);
            _ = LoadAsync();
        }
        catch (Exception ex)
        {
            _toast($"导入失败：{ex.Message}", true);
        }
    }

    private void DeleteEntry(FolderEntry entry)
    {
        var result = MessageBox.Show($"确定要删除「{entry.Name}」吗？\n此操作不可撤销。", "删除确认",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (result != MessageBoxResult.OK)
        {
            return;
        }

        try
        {
            if (entry.IsDirectory)
            {
                Directory.Delete(entry.Path, recursive: true);
            }
            else
            {
                File.Delete(entry.Path);
            }

            _toast($"已删除 {entry.Name}。", false);
            _ = LoadAsync();
        }
        catch (Exception ex)
        {
            _toast($"删除失败：{ex.Message}", true);
        }
    }

    private void Backup(FolderEntry entry)
    {
        try
        {
            var backupDirectory = Path.Combine(AppPaths.DataDirectory, "backups", "saves");
            Directory.CreateDirectory(backupDirectory);
            var target = Path.Combine(backupDirectory,
                $"{Path.GetFileName(entry.Path)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
            ZipFile.CreateFromDirectory(entry.Path, target, CompressionLevel.Optimal, includeBaseDirectory: true);
            _toast($"存档已备份到 {target}", false);
        }
        catch (Exception ex)
        {
            _toast($"备份失败：{ex.Message}", true);
        }
    }

    private static string? FindPackIcon(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                var icon = Path.Combine(path, "pack.png");
                return File.Exists(icon) ? icon : null;
            }

            using var archive = ZipFile.OpenRead(path);
            var entry = archive.Entries.FirstOrDefault(e =>
                string.Equals(e.FullName.Replace('\\', '/'), "pack.png", StringComparison.OrdinalIgnoreCase));
            if (entry == null || entry.Length == 0 || entry.Length > 2 * 1024 * 1024)
            {
                return null;
            }

            var cache = Path.Combine(AppPaths.DataDirectory, "cache", "pack-icons");
            Directory.CreateDirectory(cache);
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
                System.Text.Encoding.UTF8.GetBytes(path)))[..16];
            var target = Path.Combine(cache, hash + ".png");
            if (!File.Exists(target))
            {
                using var source = entry.Open();
                using var file = File.Create(target);
                source.CopyTo(file);
            }

            return target;
        }
        catch
        {
            return null;
        }
    }
}

internal static class FolderEntryExtensions
{
    public static int SectionPriority(this FolderEntry entry)
        => entry.ImagePath != null ? 1 : 0;
}
