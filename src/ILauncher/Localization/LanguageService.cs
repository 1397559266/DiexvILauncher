using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace ILauncher.Localization;

public static class LanguageService
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    private static readonly Dictionary<string, string> EnglishText = LoadEnglish();
    private static readonly Dictionary<string, string> ChineseText = BuildReverse(EnglishText);
    private static bool _initialized;

    public static string CurrentLanguage { get; private set; } = Chinese;

    public static void Initialize()
    {
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => TranslateElement((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(TextBlock), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => TranslateElement((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(ContentControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => TranslateElement((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(AccessText), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => TranslateElement((DependencyObject)sender)));
    }

    public static void SetLanguage(string? language)
    {
        CurrentLanguage = string.Equals(language, English, StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;
        Apply();
    }

    public static void Apply()
    {
        ApplyToOpenWindows();
        // WPF creates parts of templates after the page's first layout pass.
        // Run a couple of delayed passes so labels that appear later are localized too.
        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(120)
        };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ApplyToOpenWindows();
        };
        timer.Start();
    }

    public static string Translate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? "";
        }

        var dictionary = CurrentLanguage == English ? EnglishText : ChineseText;
        return dictionary.TryGetValue(text, out var translated) ? translated : text;
    }

    private static void ApplyToOpenWindows()
    {
        var app = Application.Current;
        if (app == null)
        {
            return;
        }

        foreach (Window window in app.Windows)
        {
            TranslateTree(window);
        }
    }

    private static void TranslateTree(DependencyObject root)
    {
        TranslateElement(root);
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            TranslateTree(VisualTreeHelper.GetChild(root, i));
        }
    }

    private static void TranslateElement(DependencyObject element)
    {
        switch (element)
        {
            case Window window when !IsBound(window, Window.TitleProperty):
                window.Title = Translate(window.Title);
                break;
            case TextBlock textBlock:
                if (!IsBound(textBlock, TextBlock.TextProperty))
                {
                    textBlock.Text = Translate(textBlock.Text);
                }

                foreach (var inline in textBlock.Inlines.OfType<Run>().ToList())
                {
                    if (!IsBound(inline, Run.TextProperty))
                    {
                        inline.Text = Translate(inline.Text);
                    }
                }

                break;
            case ContentControl contentControl when !IsBound(contentControl, ContentControl.ContentProperty):
                if (contentControl.Content is string content)
                {
                    contentControl.Content = Translate(content);
                }

                break;
            case HeaderedContentControl headered when !IsBound(headered, HeaderedContentControl.HeaderProperty):
                if (headered.Header is string header)
                {
                    headered.Header = Translate(header);
                }

                break;
            case AccessText accessText when !IsBound(accessText, AccessText.TextProperty):
                accessText.Text = Translate(accessText.Text);
                break;
        }

        if (element is DependencyObject dependencyObject)
        {
            var toolTip = ToolTipService.GetToolTip(dependencyObject);
            if (toolTip is string toolTipText)
            {
                ToolTipService.SetToolTip(dependencyObject, Translate(toolTipText));
            }
        }
    }

    private static bool IsBound(DependencyObject target, DependencyProperty property)
        => BindingOperations.GetBindingExpressionBase(target, property) != null;

    private static Dictionary<string, string> LoadEnglish()
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Localization", "en-US.json");
            if (File.Exists(path))
            {
                return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))
                       ?? new Dictionary<string, string>();
            }
        }
        catch
        {
            // A missing or malformed language file leaves the UI in Chinese.
        }

        return new Dictionary<string, string>();
    }

    private static Dictionary<string, string> BuildReverse(Dictionary<string, string> source)
    {
        var result = new Dictionary<string, string>();
        foreach (var (key, value) in source)
        {
            result.TryAdd(value, key);
        }

        return result;
    }
}
