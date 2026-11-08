using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ILauncher.Core.Services;
using ILauncher.Models;

namespace ILauncher.Services;

public sealed class SkinService
{
    public static readonly string[] BuiltInNames =
    {
        "Steve", "Alex", "Ari", "Efe", "Kai", "Makena", "Noor", "Sunny", "Zuri"
    };

    private readonly Dictionary<string, BitmapSource> _headCache = new();
    private const string BuiltinVersion = "official-1";

    private static bool IsSlim(string name)
        => name is "Alex" or "Efe" or "Makena" or "Sunny";

    public SkinService()
    {
        SkinsDirectory = Path.Combine(AppPaths.DataDirectory, "skins");
        Directory.CreateDirectory(SkinsDirectory);
        EnsureBuiltIns();
    }

    public string SkinsDirectory { get; }

    public string DefaultSkinPath => Path.Combine(SkinsDirectory, "Steve.png");

    public IReadOnlyList<SkinEntry> LoadLibrary(string? selectedPath = null)
    {
        var entries = new List<SkinEntry>();
        foreach (var name in BuiltInNames)
        {
            var path = Path.Combine(SkinsDirectory, name + ".png");
            if (File.Exists(path))
            {
                entries.Add(new SkinEntry
                {
                    Name = name,
                    Path = path,
                    Model = IsSlim(name) ? "Alex" : "Steve",
                    IsBuiltIn = true,
                    IsSelected = string.Equals(path, selectedPath, StringComparison.OrdinalIgnoreCase)
                });
            }
        }

        foreach (var file in Directory.EnumerateFiles(SkinsDirectory, "*.png")
                     .Where(f => !f.EndsWith("-cape.png", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
        {
            var name = Path.GetFileNameWithoutExtension(file);
            if (BuiltInNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            {
                continue;
            }

            entries.Add(new SkinEntry
            {
                Name = name,
                Path = file,
                Model = name.Contains("alex", StringComparison.OrdinalIgnoreCase) ? "Alex" : "Steve",
                IsBuiltIn = false,
                IsSelected = string.Equals(file, selectedPath, StringComparison.OrdinalIgnoreCase)
            });
        }

        return entries;
    }

    public string Resolve(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? path : DefaultSkinPath;

    public BitmapSource GetHead(string? skinPath, int pixelSize = 128, bool showHat = true)
    {
        var path = Resolve(skinPath);
        var key = $"{path}|{File.GetLastWriteTimeUtc(path).Ticks}|{pixelSize}|{showHat}";
        if (_headCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        try
        {
            var skin = LoadBitmap(path);
            var baseHead = new CroppedBitmap(skin, new Int32Rect(8, 8, 8, 8));
            CroppedBitmap? hat = null;
            if (showHat && skin.PixelWidth >= 64 && skin.PixelHeight >= 64)
            {
                hat = new CroppedBitmap(skin, new Int32Rect(40, 8, 8, 8));
            }

            var bitmap = RenderHeadNearest(baseHead, hat, pixelSize);
            _headCache[key] = bitmap;
            return bitmap;
        }
        catch
        {
            var fallback = Path.Combine(SkinsDirectory, "Steve.png");
            if (!string.Equals(path, fallback, StringComparison.OrdinalIgnoreCase))
            {
                return GetHead(fallback, pixelSize, showHat);
            }

            return CreateEmptyHead(pixelSize);
        }
    }

    public BitmapSource? GetSkin(string? skinPath)
    {
        try
        {
            return LoadBitmap(Resolve(skinPath));
        }
        catch
        {
            return null;
        }
    }

    public BitmapSource? GetCape(string? capePath)
    {
        if (string.IsNullOrWhiteSpace(capePath) || !File.Exists(capePath))
        {
            return null;
        }

        try
        {
            return LoadBitmap(capePath);
        }
        catch
        {
            return null;
        }
    }

    public bool Validate(string path, out string error)
    {
        error = "";
        try
        {
            var image = LoadBitmap(path);
            if (image.PixelWidth != 64 || image.PixelHeight is not (32 or 64))
            {
                error = "皮肤尺寸必须是 64×64（或旧版 64×32）。";
                return false;
            }

            if (new FileInfo(path).Length > 2 * 1024 * 1024)
            {
                error = "皮肤文件过大（超过 2 MB）。";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = $"无法读取皮肤：{ex.Message}";
            return false;
        }
    }

    public string Import(string sourcePath, string? name = null)
    {
        var baseName = string.IsNullOrWhiteSpace(name)
            ? Path.GetFileNameWithoutExtension(sourcePath)
            : name!;
        foreach (var invalid in Path.GetInvalidFileNameChars())
        {
            baseName = baseName.Replace(invalid, '_');
        }

        if (string.IsNullOrWhiteSpace(baseName))
        {
            baseName = "Skin";
        }

        var target = Path.Combine(SkinsDirectory, baseName + ".png");
        var index = 2;
        while (File.Exists(target))
        {
            target = Path.Combine(SkinsDirectory, $"{baseName} ({index++}).png");
        }

        File.Copy(sourcePath, target);
        return target;
    }

    public string SaveDownloaded(byte[] bytes, string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        var target = Path.Combine(SkinsDirectory, $"{safe}-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        File.WriteAllBytes(target, bytes);
        return target;
    }

    public string SaveCape(byte[] bytes, string name)
    {
        var safe = string.Join("_", name.Split(Path.GetInvalidFileNameChars()));
        var target = Path.Combine(SkinsDirectory, $"{safe}-cape-{DateTime.Now:yyyyMMdd-HHmmss}.png");
        File.WriteAllBytes(target, bytes);
        return target;
    }

    public string? FindCapeFor(string skinPath)
    {
        var name = Path.GetFileNameWithoutExtension(skinPath);
        var cape = Path.Combine(SkinsDirectory, name + "-cape.png");
        return File.Exists(cape) ? cape : null;
    }

    private void EnsureBuiltIns()
    {
        var marker = Path.Combine(SkinsDirectory, ".builtin-skins-version");
        var refresh = !File.Exists(marker) ||
                      !string.Equals(File.ReadAllText(marker).Trim(), BuiltinVersion, StringComparison.Ordinal);

        foreach (var name in BuiltInNames)
        {
            var target = Path.Combine(SkinsDirectory, name + ".png");
            if (File.Exists(target) && !refresh)
            {
                continue;
            }

            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/Skins/{name}.png", UriKind.Absolute);
                var resource = Application.GetResourceStream(uri);
                if (resource == null)
                {
                    continue;
                }

                using var stream = resource.Stream;
                using var file = File.Create(target);
                stream.CopyTo(file);
            }
            catch
            {
                // A missing built-in skin falls back to Steve.
            }

            var capeTarget = Path.Combine(SkinsDirectory, name + "-cape.png");
            if (File.Exists(capeTarget))
            {
                continue;
            }

            try
            {
                var uri = new Uri($"pack://application:,,,/Assets/Skins/{name}-cape.png", UriKind.Absolute);
                var resource = Application.GetResourceStream(uri);
                if (resource == null)
                {
                    continue;
                }

                using var stream = resource.Stream;
                using var file = File.Create(capeTarget);
                stream.CopyTo(file);
            }
            catch
            {
                // Capes are optional.
            }
        }

        try
        {
            File.WriteAllText(marker, BuiltinVersion);
        }
        catch
        {
            // A read-only data directory only skips future built-in refreshes.
        }
    }

    private static BitmapSource RenderHeadNearest(BitmapSource baseHead, BitmapSource? hat, int size)
    {
        const int sourceSize = 8;
        var baseFormatted = new FormatConvertedBitmap(baseHead, PixelFormats.Bgra32, null, 0);
        var basePixels = new byte[sourceSize * sourceSize * 4];
        baseFormatted.CopyPixels(basePixels, sourceSize * 4, 0);

        byte[]? hatPixels = null;
        if (hat != null)
        {
            var hatFormatted = new FormatConvertedBitmap(hat, PixelFormats.Bgra32, null, 0);
            hatPixels = new byte[sourceSize * sourceSize * 4];
            hatFormatted.CopyPixels(hatPixels, sourceSize * 4, 0);
        }

        var stride = size * 4;
        var output = new byte[stride * size];
        for (var y = 0; y < size; y++)
        {
            var sourceY = y * sourceSize / size;
            for (var x = 0; x < size; x++)
            {
                var sourceX = x * sourceSize / size;
                var sourceOffset = (sourceY * sourceSize + sourceX) * 4;
                var targetOffset = y * stride + x * 4;
                var b = basePixels[sourceOffset];
                var g = basePixels[sourceOffset + 1];
                var r = basePixels[sourceOffset + 2];
                var a = basePixels[sourceOffset + 3];
                if (hatPixels != null)
                {
                    var hatAlpha = hatPixels[sourceOffset + 3];
                    if (hatAlpha > 0)
                    {
                        var alpha = hatAlpha / 255d;
                        b = (byte)(hatPixels[sourceOffset] * alpha + b * (1 - alpha));
                        g = (byte)(hatPixels[sourceOffset + 1] * alpha + g * (1 - alpha));
                        r = (byte)(hatPixels[sourceOffset + 2] * alpha + r * (1 - alpha));
                        a = (byte)Math.Min(255, a + hatAlpha * (255 - a) / 255);
                    }
                }

                output[targetOffset] = b;
                output[targetOffset + 1] = g;
                output[targetOffset + 2] = r;
                output[targetOffset + 3] = a;
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, output, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapImage LoadBitmap(string path)    {
        using var stream = File.OpenRead(path);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private static BitmapSource CreateEmptyHead(int size)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0xE8, 0xF1, 0xFD)), null, new Rect(0, 0, size, size));
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }
}

