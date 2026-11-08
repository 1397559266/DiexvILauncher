using System.IO;
using System.IO.Compression;
using System.Text.Json;
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
    private const string BuiltinVersion = "client-extract-1";

    private static bool IsSlim(string name)
        => name is "Alex" or "Efe" or "Makena" or "Sunny";

    public SkinService()
    {
        SkinsDirectory = Path.Combine(AppPaths.DataDirectory, "skins");
        Directory.CreateDirectory(SkinsDirectory);
        EnsureBuiltIns();
    }

    public string SkinsDirectory { get; }

    public string DefaultSkinPath
        => File.Exists(Path.Combine(SkinsDirectory, "Steve.png"))
            ? Path.Combine(SkinsDirectory, "Steve.png")
            : Path.Combine(SkinsDirectory, "Default.png");

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
        var steve = Path.Combine(SkinsDirectory, "Steve.png");
        var allPresent = BuiltInNames.All(name => File.Exists(Path.Combine(SkinsDirectory, name + ".png")));
        if (!refresh && allPresent && File.Exists(steve))
        {
            return;
        }

        var extracted = TryExtractFromClientJar(refresh);
        if (extracted == 0 && !File.Exists(DefaultSkinPath))
        {
            CreateDefaultPlaceholder(DefaultSkinPath);
        }

        if (extracted > 0)
        {
            try
            {
                File.WriteAllText(marker, BuiltinVersion);
            }
            catch
            {
                // A read-only data directory only skips future extractions.
            }
        }
    }

    private int TryExtractFromClientJar(bool refresh)
    {
        var total = 0;
        foreach (var root in CandidateMinecraftRoots())
        {
            var versions = Path.Combine(root, "versions");
            if (!Directory.Exists(versions))
            {
                continue;
            }

            foreach (var versionDirectory in Directory.EnumerateDirectories(versions))
            {
                var versionName = Path.GetFileName(versionDirectory);
                var preferred = Path.Combine(versionDirectory, versionName + ".jar");
                var jars = new List<string>();
                if (File.Exists(preferred))
                {
                    jars.Add(preferred);
                }

                jars.AddRange(Directory.EnumerateFiles(versionDirectory, "*.jar")
                    .Where(path => !string.Equals(path, preferred, StringComparison.OrdinalIgnoreCase)));

                foreach (var jar in jars)
                {
                    try
                    {
                        using var archive = ZipFile.OpenRead(jar);
                        total += ExtractSkinEntries(archive, refresh);
                        if (BuiltInNames.All(name => File.Exists(Path.Combine(SkinsDirectory, name + ".png"))))
                        {
                            return total;
                        }
                    }
                    catch
                    {
                        // A damaged/patched jar is skipped.
                    }
                }
            }
        }

        return total;
    }

    private int ExtractSkinEntries(ZipArchive archive, bool refresh)
    {
        var extracted = 0;
        foreach (var name in BuiltInNames)
        {
            var target = Path.Combine(SkinsDirectory, name + ".png");
            if (File.Exists(target) && !refresh)
            {
                continue;
            }

            var lower = name.ToLowerInvariant();
            var model = IsSlim(name) ? "slim" : "wide";
            var candidates = new[]
            {
                $"assets/minecraft/textures/entity/player/{model}/{lower}.png",
                $"assets/minecraft/textures/entity/player/{lower}.png",
                $"assets/minecraft/textures/entity/{lower}.png"
            };

            var entry = candidates
                .Select(path => archive.Entries.FirstOrDefault(item =>
                    string.Equals(item.FullName, path, StringComparison.OrdinalIgnoreCase)))
                .FirstOrDefault(found => found != null);
            if (entry == null || entry.Length == 0)
            {
                continue;
            }

            try
            {
                using var source = entry.Open();
                using var file = File.Create(target);
                source.CopyTo(file);
                extracted++;
            }
            catch
            {
                // Leave the previous file intact if extraction fails.
            }
        }

        return extracted;
    }

    private IReadOnlyList<string> CandidateMinecraftRoots()
    {
        var roots = new List<string>();
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(AppPaths.SettingsFile));
                if (document.RootElement.TryGetProperty("MinecraftRoot", out var root) &&
                    root.ValueKind == JsonValueKind.String)
                {
                    var path = root.GetString();
                    if (!string.IsNullOrWhiteSpace(path))
                    {
                        roots.Add(path);
                    }
                }
            }
        }
        catch
        {
            // Fall through to the standard locations.
        }

        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft"));
        roots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".minecraft"));
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static void CreateDefaultPlaceholder(string path)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x35, 0x40)), null,
                new Rect(0, 0, 64, 64));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x8C, 0x99, 0xAA)), null,
                new Rect(8, 8, 48, 40));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x35, 0x40)), null,
                new Rect(18, 22, 8, 6));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x35, 0x40)), null,
                new Rect(38, 22, 8, 6));
            context.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2F, 0x35, 0x40)), null,
                new Rect(24, 38, 16, 4));
        }

        var bitmap = new RenderTargetBitmap(64, 64, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }

    private static BitmapSource RenderHeadNearest(BitmapSource baseHead, BitmapSource? hat, int size)
    {
        // PCL draws the face at 48/64 and the hat layer at 56/64, both centered.
        // Use a manual nearest-neighbour copy so the 8x8 skin stays crisp;
        // RenderTargetBitmap would resample the result and blur it.
        var baseFormatted = new FormatConvertedBitmap(baseHead, PixelFormats.Bgra32, null, 0);
        var basePixels = new byte[8 * 8 * 4];
        baseFormatted.CopyPixels(basePixels, 8 * 4, 0);

        byte[]? hatPixels = null;
        if (hat != null)
        {
            var hatFormatted = new FormatConvertedBitmap(hat, PixelFormats.Bgra32, null, 0);
            hatPixels = new byte[8 * 8 * 4];
            hatFormatted.CopyPixels(hatPixels, 8 * 4, 0);
        }

        var stride = size * 4;
        var output = new byte[stride * size];
        var baseSize = Math.Max(1, (int)Math.Round(size * 48d / 64d));
        var hatSize = Math.Max(1, (int)Math.Round(size * 56d / 64d));
        var baseOffset = (size - baseSize) / 2;
        var hatOffset = (size - hatSize) / 2;

        for (var y = 0; y < baseSize; y++)
        {
            var sourceY = y * 8 / baseSize;
            for (var x = 0; x < baseSize; x++)
            {
                var sourceX = x * 8 / baseSize;
                var sourceOffset = (sourceY * 8 + sourceX) * 4;
                var targetOffset = (baseOffset + y) * stride + (baseOffset + x) * 4;
                output[targetOffset] = basePixels[sourceOffset];
                output[targetOffset + 1] = basePixels[sourceOffset + 1];
                output[targetOffset + 2] = basePixels[sourceOffset + 2];
                output[targetOffset + 3] = basePixels[sourceOffset + 3];
            }
        }

        if (hatPixels != null)
        {
            for (var y = 0; y < hatSize; y++)
            {
                var sourceY = y * 8 / hatSize;
                for (var x = 0; x < hatSize; x++)
                {
                    var sourceX = x * 8 / hatSize;
                    var sourceOffset = (sourceY * 8 + sourceX) * 4;
                    var alpha = hatPixels[sourceOffset + 3];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    var targetOffset = (hatOffset + y) * stride + (hatOffset + x) * 4;
                    var inverse = 255 - alpha;
                    output[targetOffset] = (byte)((hatPixels[sourceOffset] * alpha +
                                                    output[targetOffset] * inverse) / 255);
                    output[targetOffset + 1] = (byte)((hatPixels[sourceOffset + 1] * alpha +
                                                        output[targetOffset + 1] * inverse) / 255);
                    output[targetOffset + 2] = (byte)((hatPixels[sourceOffset + 2] * alpha +
                                                        output[targetOffset + 2] * inverse) / 255);
                    output[targetOffset + 3] = (byte)Math.Min(255, alpha + output[targetOffset + 3] * inverse / 255);
                }
            }
        }

        var bitmap = BitmapSource.Create(size, size, 96, 96, PixelFormats.Bgra32, null, output, stride);
        bitmap.Freeze();
        return bitmap;
    }

    private static BitmapSource RenderHead(BitmapSource skin, int size, bool showHat)
    {
        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            var front = new Rect(size * 0.18, size * 0.30, size * 0.62, size * 0.62);
            var offset = size * 0.12;
            var top = new StreamGeometry();
            using (var geometry = top.Open())
            {
                geometry.BeginFigure(new Point(front.X + offset, front.Y - offset), true, true);
                geometry.LineTo(new Point(front.Right + offset, front.Y - offset), true, false);
                geometry.LineTo(new Point(front.Right, front.Y), true, false);
                geometry.LineTo(new Point(front.X, front.Y), true, false);
            }

            top.Freeze();
            var side = new StreamGeometry();
            using (var geometry = side.Open())
            {
                geometry.BeginFigure(new Point(front.Right, front.Y), true, true);
                geometry.LineTo(new Point(front.Right + offset, front.Y - offset), true, false);
                geometry.LineTo(new Point(front.Right + offset, front.Bottom - offset), true, false);
                geometry.LineTo(new Point(front.Right, front.Bottom), true, false);
            }

            side.Freeze();

            void DrawFace(Rect baseRect, Rect hatRect, Geometry geometry)
            {
                context.DrawGeometry(new ImageBrush(new CroppedBitmap(skin, ToRect(baseRect)))
                {
                    Stretch = Stretch.Fill
                }, null, geometry);
                if (showHat && skin.PixelWidth >= 64 && skin.PixelHeight >= 64)
                {
                    context.DrawGeometry(new ImageBrush(new CroppedBitmap(skin, ToRect(hatRect)))
                    {
                        Stretch = Stretch.Fill
                    }, null, geometry);
                }
            }

            DrawFace(new Rect(8, 8, 8, 8), new Rect(40, 8, 8, 8), new RectangleGeometry(front));
            DrawFace(new Rect(8, 0, 8, 8), new Rect(40, 0, 8, 8), top);
            DrawFace(new Rect(0, 8, 8, 8), new Rect(32, 8, 8, 8), side);
        }

        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        return bitmap;
    }

    private static Int32Rect ToRect(Rect rect)
        => new((int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height);

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

