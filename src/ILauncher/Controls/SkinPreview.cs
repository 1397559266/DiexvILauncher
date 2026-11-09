using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Media.Media3D;
using System.Windows.Shapes;
using System.Windows.Threading;
using ILauncher.Services;

namespace ILauncher.Controls;

/// <summary>A small interactive 3D Minecraft skin preview (drag to rotate, wheel to zoom).</summary>
public sealed class SkinPreview : UserControl
{
    public static readonly DependencyProperty SkinPathProperty = DependencyProperty.Register(
        nameof(SkinPath), typeof(string), typeof(SkinPreview),
        new FrameworkPropertyMetadata(null, OnModelChanged));

    public static readonly DependencyProperty CapePathProperty = DependencyProperty.Register(
        nameof(CapePath), typeof(string), typeof(SkinPreview),
        new FrameworkPropertyMetadata(null, OnModelChanged));

    public static readonly DependencyProperty SkinModelProperty = DependencyProperty.Register(
        nameof(SkinModel), typeof(string), typeof(SkinPreview),
        new FrameworkPropertyMetadata("Steve", OnModelChanged));

    public static readonly DependencyProperty AutoRotateProperty = DependencyProperty.Register(
        nameof(AutoRotate), typeof(bool), typeof(SkinPreview),
        new FrameworkPropertyMetadata(true, OnAutoRotateChanged));

    private readonly Viewport3D _viewport = new();
    private readonly ModelVisual3D _modelVisual = new();
    private readonly PerspectiveCamera _camera;
    private readonly AxisAngleRotation3D _rotation = new(new Vector3D(0, 1, 0), -18);
    private readonly DispatcherTimer _timer = new(DispatcherPriority.Render);
    private readonly TextBlock _hint;
    private Point _lastPoint;
    private bool _dragging;
    private double _angle = -18;
    private double _distance = 54;

    public SkinPreview()
    {
        ClipToBounds = true;
        var grid = new Grid();

        var background = new LinearGradientBrush(
            Color.FromRgb(0xF7, 0xFB, 0xFF),
            Color.FromRgb(0xE7, 0xF2, 0xFF),
            new Point(0, 0), new Point(1, 1));
        grid.Background = background;

        var glow = new Ellipse
        {
            Width = 260,
            Height = 260,
            Fill = new RadialGradientBrush(Color.FromArgb(40, 0x13, 0x70, 0xF3), Colors.Transparent),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        grid.Children.Add(glow);

        _camera = new PerspectiveCamera
        {
            FieldOfView = 36,
            Position = new Point3D(0, 17, _distance),
            LookDirection = new Vector3D(0, 0, -_distance),
            UpDirection = new Vector3D(0, 1, 0),
            NearPlaneDistance = 1,
            FarPlaneDistance = 200
        };
        _viewport.Camera = _camera;

        var lights = new ModelVisual3D
        {
            Content = new Model3DGroup
            {
                Children =
                {
                    new AmbientLight(Color.FromRgb(0x9A, 0x9A, 0x9A)),
                    new DirectionalLight(Colors.White, new Vector3D(-1, -1.5, -1.2)),
                    new DirectionalLight(Color.FromRgb(0x70, 0x80, 0x96), new Vector3D(1, -0.2, 1))
                }
            }
        };
        _viewport.Children.Add(lights);
        _viewport.Children.Add(_modelVisual);

        grid.Children.Add(_viewport);

        _hint = new TextBlock
        {
            Text = "拖动旋转 · 滚轮缩放",
            FontSize = 9,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0x4B, 0x60, 0x7A)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 12, 10),
            IsHitTestVisible = false
        };
        grid.Children.Add(_hint);

        Content = grid;

        _modelVisual.Transform = new RotateTransform3D(_rotation) { CenterX = 0, CenterY = 16, CenterZ = 0 };

        MouseLeftButtonDown += OnMouseDown;
        MouseLeftButtonUp += OnMouseUp;
        MouseMove += OnMouseMove;
        MouseWheel += OnMouseWheel;
        Loaded += (_, _) =>
        {
            Rebuild();
            if (AutoRotate)
            {
                _timer.Start();
            }
        };
        Unloaded += (_, _) => _timer.Stop();

        _timer.Interval = TimeSpan.FromMilliseconds(33);
        _timer.Tick += (_, _) =>
        {
            if (!AutoRotate || _dragging)
            {
                return;
            }

            _angle += 0.35;
            _rotation.Angle = _angle;
        };
        if (AutoRotate)
        {
            _timer.Start();
        }
    }

    public string? SkinPath
    {
        get => (string?)GetValue(SkinPathProperty);
        set => SetValue(SkinPathProperty, value);
    }

    public string? CapePath
    {
        get => (string?)GetValue(CapePathProperty);
        set => SetValue(CapePathProperty, value);
    }

    public string SkinModel
    {
        get => (string)GetValue(SkinModelProperty);
        set => SetValue(SkinModelProperty, value);
    }

    public bool AutoRotate
    {
        get => (bool)GetValue(AutoRotateProperty);
        set => SetValue(AutoRotateProperty, value);
    }

    private static void OnModelChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SkinPreview)d).Rebuild();

    private static void OnAutoRotateChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var preview = (SkinPreview)d;
        if ((bool)e.NewValue)
        {
            preview._timer.Start();
        }
        else
        {
            preview._timer.Stop();
        }
    }

    public void Rebuild()
    {
        var service = (Application.Current as App)?.Skins;
        if (service == null)
        {
            return;
        }

        var skin = service.GetSkin(SkinPath) ?? service.GetSkin(service.DefaultSkinPath);
        if (skin == null)
        {
            return;
        }

        // WPF 3D samples textures with linear filtering. Upscaling the pixel-art
        // texture with nearest-neighbour first keeps the 3D preview crisp instead
        // of turning faces into a blurry smear.
        var skinSource = UpscaleNearest(skin, 16);
        var group = new Model3DGroup();
        var slim = string.Equals(SkinModel, "Alex", StringComparison.OrdinalIgnoreCase);
        var armWidth = slim ? 3d : 4d;
        var armX = 4 + armWidth / 2d;
        var overlaySkin = skin.PixelHeight >= 64 ? UpscaleNearest(CompositeOverlay(skin), 16) : null;

        var headOverlay = new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(40, 8, 8, 8),
            [Face.Back] = new Rect(56, 8, 8, 8),
            [Face.Right] = new Rect(32, 8, 8, 8),
            [Face.Left] = new Rect(48, 8, 8, 8),
            [Face.Top] = new Rect(40, 0, 8, 8),
            [Face.Bottom] = new Rect(48, 0, 8, 8)
        };
        var bodyOverlay = new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(20, 36, 8, 12),
            [Face.Back] = new Rect(32, 36, 8, 12),
            [Face.Right] = new Rect(16, 36, 4, 12),
            [Face.Left] = new Rect(28, 36, 4, 12),
            [Face.Top] = new Rect(20, 32, 8, 4),
            [Face.Bottom] = new Rect(28, 32, 8, 4)
        };
        var rightArmOverlay = new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(44, 36, 4, 12),
            [Face.Back] = new Rect(52, 36, 4, 12),
            [Face.Right] = new Rect(40, 36, 4, 12),
            [Face.Left] = new Rect(48, 36, 4, 12),
            [Face.Top] = new Rect(44, 32, 4, 4),
            [Face.Bottom] = new Rect(48, 32, 4, 4)
        };
        var leftArmOverlay = new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(52, 52, 4, 12),
            [Face.Back] = new Rect(60, 52, 4, 12),
            [Face.Right] = new Rect(48, 52, 4, 12),
            [Face.Left] = new Rect(56, 52, 4, 12),
            [Face.Top] = new Rect(52, 48, 4, 4),
            [Face.Bottom] = new Rect(56, 48, 4, 4)
        };
        var rightLegOverlay = new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(4, 36, 4, 12),
            [Face.Back] = new Rect(12, 36, 4, 12),
            [Face.Right] = new Rect(0, 36, 4, 12),
            [Face.Left] = new Rect(8, 36, 4, 12),
            [Face.Top] = new Rect(4, 32, 4, 4),
            [Face.Bottom] = new Rect(8, 32, 4, 4)
        };
        var leftLegOverlay = new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(4, 52, 4, 12),
            [Face.Back] = new Rect(12, 52, 4, 12),
            [Face.Right] = new Rect(0, 52, 4, 12),
            [Face.Left] = new Rect(8, 52, 4, 12),
            [Face.Top] = new Rect(4, 48, 4, 4),
            [Face.Bottom] = new Rect(8, 48, 4, 4)
        };

        AddBox(group, new Point3D(0, 28, 0), 8, 8, 8, skinSource, new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(8, 8, 8, 8),
            [Face.Back] = new Rect(24, 8, 8, 8),
            [Face.Right] = new Rect(0, 8, 8, 8),
            [Face.Left] = new Rect(16, 8, 8, 8),
            [Face.Top] = new Rect(8, 0, 8, 8),
            [Face.Bottom] = new Rect(16, 0, 8, 8)
        }, HasOverlay(skin, headOverlay) ? overlaySkin : null, headOverlay, 0.4);

        AddBox(group, new Point3D(0, 18, 0), 8, 12, 4, skinSource, new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(20, 20, 8, 12),
            [Face.Back] = new Rect(32, 20, 8, 12),
            [Face.Right] = new Rect(16, 20, 4, 12),
            [Face.Left] = new Rect(28, 20, 4, 12),
            [Face.Top] = new Rect(20, 16, 8, 4),
            [Face.Bottom] = new Rect(28, 16, 8, 4)
        }, HasOverlay(skin, bodyOverlay) ? overlaySkin : null, bodyOverlay, 0.3);

        AddBox(group, new Point3D(-armX, 18, 0), armWidth, 12, 4, skinSource, new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(44, 20, 4, 12),
            [Face.Back] = new Rect(52, 20, 4, 12),
            [Face.Right] = new Rect(40, 20, 4, 12),
            [Face.Left] = new Rect(48, 20, 4, 12),
            [Face.Top] = new Rect(44, 16, 4, 4),
            [Face.Bottom] = new Rect(48, 16, 4, 4)
        }, HasOverlay(skin, rightArmOverlay) ? overlaySkin : null, rightArmOverlay, 0.3);

        AddBox(group, new Point3D(armX, 18, 0), armWidth, 12, 4, skinSource, new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(36, 52, 4, 12),
            [Face.Back] = new Rect(44, 52, 4, 12),
            [Face.Right] = new Rect(32, 52, 4, 12),
            [Face.Left] = new Rect(40, 52, 4, 12),
            [Face.Top] = new Rect(36, 48, 4, 4),
            [Face.Bottom] = new Rect(40, 48, 4, 4)
        }, HasOverlay(skin, leftArmOverlay) ? overlaySkin : null, leftArmOverlay, 0.3);

        AddBox(group, new Point3D(-2, 6, 0), 4, 12, 4, skinSource, new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(4, 20, 4, 12),
            [Face.Back] = new Rect(12, 20, 4, 12),
            [Face.Right] = new Rect(0, 20, 4, 12),
            [Face.Left] = new Rect(8, 20, 4, 12),
            [Face.Top] = new Rect(4, 16, 4, 4),
            [Face.Bottom] = new Rect(8, 16, 4, 4)
        }, HasOverlay(skin, rightLegOverlay) ? overlaySkin : null, rightLegOverlay, 0.3);

        AddBox(group, new Point3D(2, 6, 0), 4, 12, 4, skinSource, new Dictionary<Face, Rect>
        {
            [Face.Front] = new Rect(20, 52, 4, 12),
            [Face.Back] = new Rect(28, 52, 4, 12),
            [Face.Right] = new Rect(16, 52, 4, 12),
            [Face.Left] = new Rect(24, 52, 4, 12),
            [Face.Top] = new Rect(20, 48, 4, 4),
            [Face.Bottom] = new Rect(24, 48, 4, 4)
        }, HasOverlay(skin, leftLegOverlay) ? overlaySkin : null, leftLegOverlay, 0.3);

        AddCape(group, service);

        _modelVisual.Content = group;
        _rotation.Angle = _angle;
        _modelVisual.Transform = new RotateTransform3D(_rotation) { CenterX = 0, CenterY = 16, CenterZ = 0 };
        _camera.Position = new Point3D(0, 17, _distance);
        _camera.LookDirection = new Vector3D(0, 0, -_distance);
    }

    private void AddCape(Model3DGroup group, SkinService service)
    {
        var path = CapePath;
        if (string.IsNullOrWhiteSpace(path))
        {
            path = service.FindCapeFor(service.Resolve(SkinPath));
        }

        var cape = service.GetCape(path);
        if (cape == null)
        {
            return;
        }

        var capeSource = UpscaleNearest(cape, 16);
        var material = new DiffuseMaterial(new ImageBrush(capeSource) { Stretch = Stretch.Fill });
        var mesh = new MeshGeometry3D
        {
            Positions = new Point3DCollection
            {
                new(-4.2, 24.5, -2.25),
                new(4.2, 24.5, -2.25),
                new(4.2, 9.5, -2.25),
                new(-4.2, 9.5, -2.25)
            },
            TextureCoordinates = new PointCollection
            {
                new(0, 0), new(1, 0), new(1, 1), new(0, 1)
            },
            TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
        };
        group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
    }

    private static readonly (Rect Overlay, Rect Base)[] OverlayMappings =
    {
        (new Rect(40, 8, 8, 8), new Rect(8, 8, 8, 8)),
        (new Rect(56, 8, 8, 8), new Rect(24, 8, 8, 8)),
        (new Rect(32, 8, 8, 8), new Rect(0, 8, 8, 8)),
        (new Rect(48, 8, 8, 8), new Rect(16, 8, 8, 8)),
        (new Rect(40, 0, 8, 8), new Rect(8, 0, 8, 8)),
        (new Rect(48, 0, 8, 8), new Rect(16, 0, 8, 8)),
        (new Rect(20, 36, 8, 12), new Rect(20, 20, 8, 12)),
        (new Rect(32, 36, 8, 12), new Rect(32, 20, 8, 12)),
        (new Rect(16, 36, 4, 12), new Rect(16, 20, 4, 12)),
        (new Rect(28, 36, 4, 12), new Rect(28, 20, 4, 12)),
        (new Rect(20, 32, 8, 4), new Rect(20, 16, 8, 4)),
        (new Rect(28, 32, 8, 4), new Rect(28, 16, 8, 4)),
        (new Rect(44, 36, 4, 12), new Rect(44, 20, 4, 12)),
        (new Rect(52, 36, 4, 12), new Rect(52, 20, 4, 12)),
        (new Rect(40, 36, 4, 12), new Rect(40, 20, 4, 12)),
        (new Rect(48, 36, 4, 12), new Rect(48, 20, 4, 12)),
        (new Rect(44, 32, 4, 4), new Rect(44, 16, 4, 4)),
        (new Rect(48, 32, 4, 4), new Rect(48, 16, 4, 4)),
        (new Rect(52, 52, 4, 12), new Rect(36, 52, 4, 12)),
        (new Rect(60, 52, 4, 12), new Rect(44, 52, 4, 12)),
        (new Rect(48, 52, 4, 12), new Rect(32, 52, 4, 12)),
        (new Rect(56, 52, 4, 12), new Rect(40, 52, 4, 12)),
        (new Rect(52, 48, 4, 4), new Rect(36, 48, 4, 4)),
        (new Rect(56, 48, 4, 4), new Rect(40, 48, 4, 4)),
        (new Rect(4, 36, 4, 12), new Rect(4, 20, 4, 12)),
        (new Rect(12, 36, 4, 12), new Rect(12, 20, 4, 12)),
        (new Rect(0, 36, 4, 12), new Rect(0, 20, 4, 12)),
        (new Rect(8, 36, 4, 12), new Rect(8, 20, 4, 12)),
        (new Rect(4, 32, 4, 4), new Rect(4, 16, 4, 4)),
        (new Rect(8, 32, 4, 4), new Rect(8, 16, 4, 4)),
        (new Rect(4, 52, 4, 12), new Rect(20, 52, 4, 12)),
        (new Rect(12, 52, 4, 12), new Rect(28, 52, 4, 12)),
        (new Rect(0, 52, 4, 12), new Rect(16, 52, 4, 12)),
        (new Rect(8, 52, 4, 12), new Rect(24, 52, 4, 12)),
        (new Rect(4, 48, 4, 4), new Rect(20, 48, 4, 4)),
        (new Rect(8, 48, 4, 4), new Rect(24, 48, 4, 4))
    };

    private static BitmapSource CompositeOverlay(BitmapSource skin)
    {
        var formatted = new FormatConvertedBitmap(skin, PixelFormats.Bgra32, null, 0);
        var stride = formatted.PixelWidth * 4;
        var pixels = new byte[stride * formatted.PixelHeight];
        formatted.CopyPixels(pixels, stride, 0);
        var result = new byte[pixels.Length];
        // Only pixels that belong to an overlay face are rendered as the
        // outer shell. Transparent overlay pixels stay transparent and are
        // skipped by the cutout mesh instead of writing depth over the base.
        foreach (var (overlay, baseRect) in OverlayMappings)
        {
            for (var y = 0; y < (int)overlay.Height; y++)
            {
                for (var x = 0; x < (int)overlay.Width; x++)
                {
                    var overlayOffset = ((int)overlay.Y + y) * stride + ((int)overlay.X + x) * 4;
                    var baseOffset = ((int)baseRect.Y + y) * stride + ((int)baseRect.X + x) * 4;
                    if (overlayOffset + 3 >= pixels.Length || baseOffset + 3 >= pixels.Length)
                    {
                        continue;
                    }

                    var alpha = pixels[overlayOffset + 3];
                    if (alpha == 0)
                    {
                        continue;
                    }

                    if (alpha < 255)
                    {
                        result[overlayOffset] = (byte)((pixels[overlayOffset] * alpha +
                                                        pixels[baseOffset] * (255 - alpha)) / 255);
                        result[overlayOffset + 1] = (byte)((pixels[overlayOffset + 1] * alpha +
                                                            pixels[baseOffset + 1] * (255 - alpha)) / 255);
                        result[overlayOffset + 2] = (byte)((pixels[overlayOffset + 2] * alpha +
                                                            pixels[baseOffset + 2] * (255 - alpha)) / 255);
                    }
                    else
                    {
                        result[overlayOffset] = pixels[overlayOffset];
                        result[overlayOffset + 1] = pixels[overlayOffset + 1];
                        result[overlayOffset + 2] = pixels[overlayOffset + 2];
                    }

                    result[overlayOffset + 3] = 255;
                }
            }
        }

        var image = BitmapSource.Create(formatted.PixelWidth, formatted.PixelHeight, 96, 96,
            PixelFormats.Bgra32, null, result, stride);
        image.Freeze();
        return image;
    }

    private static BitmapSource UpscaleNearest(BitmapSource source, int scale)
    {
        if (scale <= 1)
        {
            return source;
        }

        var formatted = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
        var width = formatted.PixelWidth;
        var height = formatted.PixelHeight;
        var stride = width * 4;
        var pixels = new byte[stride * height];
        formatted.CopyPixels(pixels, stride, 0);

        var scaledWidth = width * scale;
        var scaledStride = scaledWidth * 4;
        var scaled = new byte[scaledStride * height * scale];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var sourceOffset = y * stride + x * 4;
                for (var dy = 0; dy < scale; dy++)
                {
                    var row = (y * scale + dy) * scaledStride;
                    for (var dx = 0; dx < scale; dx++)
                    {
                        var targetOffset = row + (x * scale + dx) * 4;
                        scaled[targetOffset] = pixels[sourceOffset];
                        scaled[targetOffset + 1] = pixels[sourceOffset + 1];
                        scaled[targetOffset + 2] = pixels[sourceOffset + 2];
                        scaled[targetOffset + 3] = pixels[sourceOffset + 3];
                    }
                }
            }
        }

        var result = BitmapSource.Create(scaledWidth, height * scale, 96, 96,
            PixelFormats.Bgra32, null, scaled, scaledStride);
        result.Freeze();
        return result;
    }

    private static bool HasOverlay(BitmapSource skin, IReadOnlyDictionary<Face, Rect> map)
    {
        if (skin.PixelHeight < 64 || skin.PixelWidth < 64)
        {
            return false;
        }

        try
        {
            var formatted = new FormatConvertedBitmap(skin, PixelFormats.Bgra32, null, 0);
            var stride = formatted.PixelWidth * 4;
            var pixels = new byte[stride * formatted.PixelHeight];
            formatted.CopyPixels(pixels, stride, 0);
            foreach (var rect in map.Values)
            {
                for (var y = (int)rect.Y; y < rect.Bottom && y < formatted.PixelHeight; y++)
                {
                    for (var x = (int)rect.X; x < rect.Right && x < formatted.PixelWidth; x++)
                    {
                        if (pixels[y * stride + x * 4 + 3] > 0)
                        {
                            return true;
                        }
                    }
                }
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static void AddBox(Model3DGroup group, Point3D center, double width, double height, double depth,
        ImageSource skin, IReadOnlyDictionary<Face, Rect> uv,
        ImageSource? overlay = null, IReadOnlyDictionary<Face, Rect>? overlayUv = null, double inflate = 0)
    {
        AddQuads(group, center, width, height, depth, skin, uv);
        if (overlay != null && overlayUv != null && overlay is BitmapSource bitmap)
        {
            AddCutoutOverlayQuads(group, center, width + inflate * 2, height + inflate * 2, depth + inflate * 2,
                bitmap, overlayUv);
        }
    }

    private static void AddCutoutOverlayQuads(Model3DGroup group, Point3D center, double width, double height,
        double depth, BitmapSource texture, IReadOnlyDictionary<Face, Rect> uv)
    {
        var formatted = new FormatConvertedBitmap(texture, PixelFormats.Bgra32, null, 0);
        var stride = formatted.PixelWidth * 4;
        var pixels = new byte[stride * formatted.PixelHeight];
        formatted.CopyPixels(pixels, stride, 0);
        var pixelScale = Math.Max(1, formatted.PixelWidth / 64);

        var x0 = center.X - width / 2;
        var x1 = center.X + width / 2;
        var y0 = center.Y - height / 2;
        var y1 = center.Y + height / 2;
        var z0 = center.Z - depth / 2;
        var z1 = center.Z + depth / 2;

        static Point3D Lerp(Point3D a, Point3D b, double amount)
            => new(a.X + (b.X - a.X) * amount, a.Y + (b.Y - a.Y) * amount, a.Z + (b.Z - a.Z) * amount);

        void AddFace(Rect rect, Point3D p0, Point3D p1, Point3D p2, Point3D p3, Vector3D normal,
            bool flipU = false)
        {
            var positions = new Point3DCollection();
            var uvs = new PointCollection();
            var indices = new Int32Collection();
            for (var y = (int)Math.Floor(rect.Y); y < (int)Math.Ceiling(rect.Bottom); y++)
            {
                for (var x = (int)Math.Floor(rect.X); x < (int)Math.Ceiling(rect.Right); x++)
                {
                    var sampleX = x * pixelScale;
                    var sampleY = y * pixelScale;
                    if (sampleX < 0 || sampleY < 0 || sampleX >= formatted.PixelWidth || sampleY >= formatted.PixelHeight ||
                        pixels[sampleY * stride + sampleX * 4 + 3] == 0)
                    {
                        continue;
                    }

                    var a0 = (x - rect.X) / rect.Width;
                    var a1 = (x + 1 - rect.X) / rect.Width;
                    var b0 = (y - rect.Y) / rect.Height;
                    var b1 = (y + 1 - rect.Y) / rect.Height;
                    var u0 = flipU ? 1 - a0 : a0;
                    var u1 = flipU ? 1 - a1 : a1;

                    Point3D At(double a, double b)
                        => Lerp(Lerp(p3, p2, a), Lerp(p0, p1, a), b);

                    var index = positions.Count;
                    positions.Add(At(a0, b1));
                    positions.Add(At(a1, b1));
                    positions.Add(At(a1, b0));
                    positions.Add(At(a0, b0));
                    uvs.Add(new Point(u0, b1));
                    uvs.Add(new Point(u1, b1));
                    uvs.Add(new Point(u1, b0));
                    uvs.Add(new Point(u0, b0));
                    indices.Add(index);
                    indices.Add(index + 1);
                    indices.Add(index + 2);
                    indices.Add(index);
                    indices.Add(index + 2);
                    indices.Add(index + 3);
                }
            }

            if (positions.Count == 0)
            {
                return;
            }

            var mesh = new MeshGeometry3D
            {
                Positions = positions,
                TextureCoordinates = uvs,
                TriangleIndices = indices,
                Normals = new Vector3DCollection(Enumerable.Repeat(normal, positions.Count))
            };
            var brush = new ImageBrush(texture)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = new Rect(rect.X / 64d, rect.Y / 64d, rect.Width / 64d, rect.Height / 64d)
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            group.Children.Add(new GeometryModel3D(mesh, new DiffuseMaterial(brush)));
        }

        if (uv.TryGetValue(Face.Front, out var front))
        {
            AddFace(front, new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1),
                new Point3D(x0, y1, z1), new Vector3D(0, 0, 1));
        }

        if (uv.TryGetValue(Face.Back, out var back))
        {
            AddFace(back, new Point3D(x1, y0, z0), new Point3D(x0, y0, z0), new Point3D(x0, y1, z0),
                new Point3D(x1, y1, z0), new Vector3D(0, 0, -1));
        }

        if (uv.TryGetValue(Face.Right, out var right))
        {
            AddFace(right, new Point3D(x1, y0, z1), new Point3D(x1, y0, z0), new Point3D(x1, y1, z0),
                new Point3D(x1, y1, z1), new Vector3D(1, 0, 0), flipU: true);
        }

        if (uv.TryGetValue(Face.Left, out var left))
        {
            AddFace(left, new Point3D(x0, y0, z0), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1),
                new Point3D(x0, y1, z0), new Vector3D(-1, 0, 0), flipU: true);
        }

        if (uv.TryGetValue(Face.Top, out var top))
        {
            AddFace(top, new Point3D(x0, y1, z1), new Point3D(x1, y1, z1), new Point3D(x1, y1, z0),
                new Point3D(x0, y1, z0), new Vector3D(0, 1, 0));
        }

        if (uv.TryGetValue(Face.Bottom, out var bottom))
        {
            AddFace(bottom, new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y0, z1),
                new Point3D(x0, y0, z1), new Vector3D(0, -1, 0));
        }
    }

    private static void AddQuads(Model3DGroup group, Point3D center, double width, double height, double depth,
        ImageSource texture, IReadOnlyDictionary<Face, Rect> uv)
    {
        var x0 = center.X - width / 2;
        var x1 = center.X + width / 2;
        var y0 = center.Y - height / 2;
        var y1 = center.Y + height / 2;
        var z0 = center.Z - depth / 2;
        var z1 = center.Z + depth / 2;

        void Quad(Point3D p0, Point3D p1, Point3D p2, Point3D p3, Vector3D normal, Rect rect,
            bool flipU = false)
        {
            // The skin net is right | front | left | back, so the right face
            // shares its u=1 edge with the front face and the left face shares
            // its u=0 edge with it. Side faces therefore need u mirrored,
            // otherwise their front/back halves are swapped.
            var uvs = flipU
                ? new PointCollection { new(1, 1), new(0, 1), new(0, 0), new(1, 0) }
                : new PointCollection { new(0, 1), new(1, 1), new(1, 0), new(0, 0) };

            var mesh = new MeshGeometry3D
            {
                Positions = new Point3DCollection { p0, p1, p2, p3 },
                Normals = new Vector3DCollection { normal, normal, normal, normal },
                TextureCoordinates = uvs,
                TriangleIndices = new Int32Collection { 0, 1, 2, 0, 2, 3 }
            };

            var brush = new ImageBrush(texture)
            {
                Stretch = Stretch.Fill,
                ViewboxUnits = BrushMappingMode.RelativeToBoundingBox,
                Viewbox = new Rect(rect.X / 64d, rect.Y / 64d, rect.Width / 64d, rect.Height / 64d)
            };
            RenderOptions.SetBitmapScalingMode(brush, BitmapScalingMode.NearestNeighbor);
            var material = new DiffuseMaterial(brush);
            group.Children.Add(new GeometryModel3D(mesh, material) { BackMaterial = material });
        }

        if (uv.TryGetValue(Face.Front, out var front))
        {
            Quad(new Point3D(x0, y0, z1), new Point3D(x1, y0, z1), new Point3D(x1, y1, z1), new Point3D(x0, y1, z1),
                new Vector3D(0, 0, 1), front);
        }

        if (uv.TryGetValue(Face.Back, out var back))
        {
            Quad(new Point3D(x1, y0, z0), new Point3D(x0, y0, z0), new Point3D(x0, y1, z0), new Point3D(x1, y1, z0),
                new Vector3D(0, 0, -1), back);
        }

        if (uv.TryGetValue(Face.Right, out var right))
        {
            Quad(new Point3D(x1, y0, z1), new Point3D(x1, y0, z0), new Point3D(x1, y1, z0), new Point3D(x1, y1, z1),
                new Vector3D(1, 0, 0), right, flipU: true);
        }

        if (uv.TryGetValue(Face.Left, out var left))
        {
            Quad(new Point3D(x0, y0, z0), new Point3D(x0, y0, z1), new Point3D(x0, y1, z1), new Point3D(x0, y1, z0),
                new Vector3D(-1, 0, 0), left, flipU: true);
        }

        if (uv.TryGetValue(Face.Top, out var top))
        {
            Quad(new Point3D(x0, y1, z1), new Point3D(x1, y1, z1), new Point3D(x1, y1, z0), new Point3D(x0, y1, z0),
                new Vector3D(0, 1, 0), top);
        }

        if (uv.TryGetValue(Face.Bottom, out var bottom))
        {
            Quad(new Point3D(x0, y0, z0), new Point3D(x1, y0, z0), new Point3D(x1, y0, z1), new Point3D(x0, y0, z1),
                new Vector3D(0, -1, 0), bottom);
        }
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        _lastPoint = e.GetPosition(this);
        CaptureMouse();
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        _dragging = false;
        ReleaseMouseCapture();
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging)
        {
            return;
        }

        var point = e.GetPosition(this);
        var delta = point.X - _lastPoint.X;
        _lastPoint = point;
        _angle += delta * 0.45;
        _rotation.Angle = _angle;
    }

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        _distance = Math.Clamp(_distance - e.Delta * 0.03, 36, 90);
        _camera.Position = new Point3D(0, 17, _distance);
        _camera.LookDirection = new Vector3D(0, 0, -_distance);
        e.Handled = true;
    }

    private enum Face
    {
        Front,
        Back,
        Left,
        Right,
        Top,
        Bottom
    }
}


