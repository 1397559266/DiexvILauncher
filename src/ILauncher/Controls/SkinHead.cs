using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ILauncher.Services;

namespace ILauncher.Controls;

/// <summary>Renders a Minecraft skin head (base + hat layer) from a skin file.</summary>
public sealed class SkinHead : Image
{
    public static readonly DependencyProperty SkinPathProperty = DependencyProperty.Register(
        nameof(SkinPath), typeof(string), typeof(SkinHead),
        new FrameworkPropertyMetadata(null, OnVisualChanged));

    public static readonly DependencyProperty ShowHatProperty = DependencyProperty.Register(
        nameof(ShowHat), typeof(bool), typeof(SkinHead),
        new FrameworkPropertyMetadata(true, OnVisualChanged));

    public SkinHead()
    {
        Stretch = Stretch.Fill;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor);
        SnapsToDevicePixels = true;
        Loaded += (_, _) => UpdateSource();
        SizeChanged += (_, _) => UpdateSource();
    }

    public string? SkinPath
    {
        get => (string?)GetValue(SkinPathProperty);
        set => SetValue(SkinPathProperty, value);
    }

    public bool ShowHat
    {
        get => (bool)GetValue(ShowHatProperty);
        set => SetValue(ShowHatProperty, value);
    }

    private static void OnVisualChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((SkinHead)d).UpdateSource();

    private void UpdateSource()
    {
        var service = (Application.Current as App)?.Skins;
        if (service == null)
        {
            return;
        }

        try
        {
            var displaySize = (int)Math.Round(Math.Max(ActualWidth, ActualHeight));
            if (displaySize <= 0)
            {
                displaySize = 128;
            }

            Source = service.GetHead(SkinPath, Math.Clamp(displaySize, 32, 256), ShowHat);
        }
        catch
        {
            Source = null;
        }
    }
}

