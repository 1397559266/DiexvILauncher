using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media.Imaging;
using ILauncher.Core.Services;
using ILauncher.Models;
using ILauncher.Services;

namespace ILauncher.Core;

public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is string text && text.Equals("invert", StringComparison.OrdinalIgnoreCase))
        {
            flag = !flag;
        }

        if (Invert)
        {
            flag = !flag;
        }

        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is Visibility.Visible;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => value is not true;
}

public sealed class NullToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var visible = value != null;
        if (Invert)
        {
            visible = !visible;
        }

        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class StringEmptyToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var empty = string.IsNullOrWhiteSpace(value as string);
        var visible = Invert ? !empty : empty;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BoolToOpacityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? 1d : 0.4d;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class ModIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not byte[] { Length: > 0 } bytes)
        {
            return null;
        }

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 64;
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

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class RemoteIconConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapSource?> Cache = new(StringComparer.Ordinal);
    private static readonly HashSet<string> Downloading = new(StringComparer.Ordinal);
    private static readonly object Gate = new();
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(15) };

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string url || string.IsNullOrWhiteSpace(url))
        {
            return null;
        }

        lock (Gate)
        {
            if (Cache.TryGetValue(url, out var cached))
            {
                return cached;
            }
        }

        var diskPath = GetDiskPath(url);
        try
        {
            if (File.Exists(diskPath))
            {
                using var stream = File.OpenRead(diskPath);
                var diskImage = new BitmapImage();
                diskImage.BeginInit();
                diskImage.CacheOption = BitmapCacheOption.OnLoad;
                diskImage.DecodePixelWidth = 96;
                diskImage.StreamSource = stream;
                diskImage.EndInit();
                diskImage.Freeze();
                lock (Gate)
                {
                    Cache[url] = diskImage;
                }

                return diskImage;
            }
        }
        catch
        {
            // Fall back to the URI image below.
        }

        StartDownload(url, diskPath);
        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.UriSource = new Uri(url, UriKind.Absolute);
            image.CacheOption = BitmapCacheOption.OnDemand;
            image.DecodePixelWidth = 96;
            image.EndInit();
            lock (Gate)
            {
                Cache[url] = image;
            }

            return image;
        }
        catch
        {
            lock (Gate)
            {
                Cache[url] = null;
            }

            return null;
        }
    }

    private static string GetDiskPath(string url)
    {
        var hash = System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        return Path.Combine(AppPaths.CacheDirectory, "icons", hash + ".img");
    }

    private static void StartDownload(string url, string path)
    {
        lock (Gate)
        {
            if (!Downloading.Add(url))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await Http.GetByteArrayAsync(url);
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                await File.WriteAllBytesAsync(temporary, bytes);
                File.Move(temporary, path, true);
            }
            catch
            {
                // Icon cache is best-effort.
            }
            finally
            {
                lock (Gate)
                {
                    Downloading.Remove(url);
                }
            }
        });
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LoaderBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        var loader = value as string ?? "";
        return loader switch
        {
            "Fabric" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC2, 0x8B, 0x3A)),
            "Forge" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4C, 0x6C, 0x9A)),
            "NeoForge" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD8, 0x7A, 0x2E)),
            "Quilt" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9B, 0x5C, 0xD6)),
            "OptiFine" => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4E, 0x8B, 0x45)),
            _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x5B, 0x7C, 0xA8))
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class LogLevelBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        return value is LogLevelKind kind
            ? kind switch
            {
                LogLevelKind.Error => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x3B, 0x42)),
                LogLevelKind.Warn => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC2, 0x82, 0x1E)),
                LogLevelKind.Debug => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x7C, 0x8A, 0x9E)),
                LogLevelKind.Launcher => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x13, 0x70, 0xF3)),
                LogLevelKind.Trace => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9A, 0xA6, 0xB6)),
                _ => new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3E, 0x6B, 0x9E))
            }
            : System.Windows.Media.Brushes.Gray;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class BlockIconConverter : IValueConverter
{
    private static readonly HashSet<string> AprilFoolsIds = new(StringComparer.OrdinalIgnoreCase)
    {
        "15w14a", "1.RV-Pre1", "3D Shareware v1.34", "20w14infinite",
        "22w13oneblockatatime", "23w13a_or_b", "24w14potato", "25w14craftmine"
    };
    private static readonly Dictionary<string, BitmapSource> Cache = new();

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ILauncher.Core.Services.RemoteVersion version)
        {
            return null;
        }

        var name = IsAprilFools(version) ? "potato" : version.Type == "release" ? "grass" : "command";
        lock (Cache)
        {
            if (Cache.TryGetValue(name, out var cached))
            {
                return cached;
            }

            try
            {
                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                var extracted = BlockTextureService.GetTexturePath(name);
                if (extracted != null)
                {
                    image.UriSource = new Uri(extracted, UriKind.Absolute);
                }
                else
                {
                    var fallback = name == "potato" ? "gold" : name;
                    image.UriSource = new Uri($"pack://application:,,,/Assets/Blocks/{fallback}.png", UriKind.Absolute);
                }

                image.EndInit();
                BitmapSource source = image;
                if (image.PixelHeight > image.PixelWidth && image.PixelHeight % image.PixelWidth == 0)
                {
                    // Animated block textures are stored as a vertical strip of
                    // square frames (e.g. command_block_front 16x64). Use the
                    // first frame as the icon.
                    source = new CroppedBitmap(image, new Int32Rect(0, 0, image.PixelWidth, image.PixelWidth));
                }

                source.Freeze();
                Cache[name] = source;
                return source;
            }
            catch
            {
                return null;
            }
        }
    }

    private static bool IsAprilFools(ILauncher.Core.Services.RemoteVersion version)
        => AprilFoolsIds.Contains(version.Id) ||
           (version.Type != "release" && version.ReleaseTime.Month == 4 && version.ReleaseTime.Day == 1);

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class InstanceIconConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not ILauncher.Core.Models.GameInstance instance)
        {
            return null;
        }

        try
        {
            return ILauncher.Services.InstanceIconService.GetIcon(instance);
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class Base64ImageConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string text || string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        try
        {
            var comma = text.IndexOf(',');
            if (text.StartsWith("data:", StringComparison.OrdinalIgnoreCase) && comma >= 0)
            {
                text = text[(comma + 1)..];
            }

            var bytes = System.Convert.FromBase64String(text);
            using var stream = new MemoryStream(bytes);
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

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class PathToThumbnailConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string path || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.DecodePixelWidth = 320;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch
        {
            return null;
        }
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

public sealed class RelativeTimeConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not DateTime time)
        {
            return "";
        }

        var span = DateTime.Now - time;
        return span.TotalMinutes switch
        {
            < 1 => "刚刚",
            < 60 => $"{(int)span.TotalMinutes} 分钟前",
            < 60 * 24 => $"{(int)span.TotalHours} 小时前",
            < 60 * 24 * 30 => $"{(int)span.TotalDays} 天前",
            _ => time.ToString("yyyy-MM-dd")
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
