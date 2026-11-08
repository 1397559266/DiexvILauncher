using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ILauncher.Core.Models;
using ILauncher.Core.Services;

namespace ILauncher.Services;

public static class InstanceIconService
{
    private static readonly Dictionary<string, BitmapSource> BlockCache = new();
    private static readonly Dictionary<string, BitmapSource> LoaderCache = new();
    private static readonly string[] Blocks = { "grass", "stone", "diamond", "redstone", "command", "gold", "planks", "obsidian" };

    public static event EventHandler<string>? Changed;

    public static string IconsDirectory => Path.Combine(AppPaths.DataDirectory, "icons");

    public static string CustomIconPath(GameInstance instance)
        => Path.Combine(IconsDirectory, AppPaths.SanitizeFileName(instance.Id) + ".png");

    public static string? FindIconPath(GameInstance instance)
    {
        var versionIcon = Path.Combine(instance.Directory, "icon.png");
        if (File.Exists(versionIcon))
        {
            return versionIcon;
        }

        var custom = CustomIconPath(instance);
        return File.Exists(custom) ? custom : null;
    }

    public static bool HasCustomIcon(GameInstance instance) => FindIconPath(instance) != null;

    public static BitmapSource GetIcon(GameInstance instance)
    {
        try
        {
            var path = FindIconPath(instance);
            if (path != null)
            {
                var custom = Load(path);
                if (custom != null)
                {
                    return custom;
                }
            }
        }
        catch
        {
            // fall through to the block icon
        }

        return GetLoaderIcon(instance.Loader) ?? GetBlockIcon(instance.Id);
    }

    public static BitmapSource? GetLoaderIcon(string loader)
    {
        var key = loader.ToLowerInvariant() switch
        {
            var value when value.Contains("neoforge") => "neoforge",
            var value when value.Contains("fabric") => "fabric",
            var value when value.Contains("quilt") => "quilt",
            var value when value.Contains("forge") => "forge",
            var value when value.Contains("optifine") => "optifine",
            _ => null
        };

        if (key == null)
        {
            return null;
        }

        lock (LoaderCache)
        {
            if (LoaderCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = new Uri($"pack://application:,,,/Assets/Loaders/{key}.png", UriKind.Absolute);
                image.EndInit();
                image.Freeze();
                LoaderCache[key] = image;
                return image;
            }
            catch
            {
                return null;
            }
        }
    }

    public static string SetIcon(GameInstance instance, string sourcePath)
    {
        var image = Load(sourcePath) ?? throw new InvalidOperationException("无法读取所选图片。");
        if (image.PixelWidth < 16 || image.PixelHeight < 16)
        {
            throw new InvalidOperationException("图标尺寸太小，请选择至少 16×16 的 PNG。");
        }

        // PCL-compatible location: versions/<id>/icon.png. Keeping the icon
        // beside the version also makes it survive an instance export.
        var target = Path.Combine(instance.Directory, "icon.png");
        if (!string.Equals(Path.GetFullPath(sourcePath), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase))
        {
            File.Copy(sourcePath, target, overwrite: true);
        }

        Changed?.Invoke(null, instance.Id);
        return target;
    }

    public static void ResetIcon(GameInstance instance)
    {
        foreach (var path in new[]
                 {
                     Path.Combine(instance.Directory, "icon.png"),
                     CustomIconPath(instance)
                 })
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }

        Changed?.Invoke(null, instance.Id);
    }

    public static BitmapSource GetBlockIcon(string id)
    {
        var hash = 17;
        foreach (var ch in id)
        {
            hash = unchecked(hash * 31 + ch);
        }

        var name = Blocks[Math.Abs(hash) % Blocks.Length];
        lock (BlockCache)
        {
            if (BlockCache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/Blocks/{name}.png", UriKind.Absolute);
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.UriSource = uri;
                image.EndInit();
                image.Freeze();
                BlockCache[name] = image;
                return image;
            }
            catch
            {
                return CreateFallback();
            }
        }
    }

    private static BitmapSource? Load(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    private static BitmapSource CreateFallback()
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xF1, 0xFD)), null,
                new Rect(0, 0, 64, 64), 10, 10);
        }

        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}
