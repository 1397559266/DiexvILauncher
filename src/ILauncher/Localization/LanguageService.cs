using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using ILauncher.Core.Models;
using ILauncher.Core.Services;

namespace ILauncher.Localization;

public static class LanguageService
{
    public const string Chinese = "zh-CN";
    public const string English = "en-US";

    private static readonly Dictionary<string, string> EnglishText = LoadEnglish();
    private static readonly Dictionary<string, string> ChineseText = BuildReverse(EnglishText);
    private static readonly List<FormatRule> EnglishFormats = BuildFormats(EnglishText, reverse: false);
    private static readonly List<FormatRule> ChineseFormats = BuildFormats(EnglishText, reverse: true);
    private static readonly ConditionalWeakTable<TextBlock, string> DynamicTextAttached = new();
    private static readonly ConditionalWeakTable<ItemsControl, string> HookedItemsControls = new();
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
            new RoutedEventHandler((sender, _) =>
            {
                var textBlock = (TextBlock)sender;
                AttachDynamicText(textBlock);
                TranslateElement(textBlock);
            }));
        EventManager.RegisterClassHandler(typeof(ContentControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => TranslateElement((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(AccessText), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => TranslateElement((DependencyObject)sender)));
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                var window = (Window)sender;
                window.Dispatcher.BeginInvoke(new Action(() => ApplyTo(window)), DispatcherPriority.Loaded);
            }));
        EventManager.RegisterClassHandler(typeof(UserControl), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) =>
            {
                var control = (UserControl)sender;
                control.Dispatcher.BeginInvoke(new Action(() => ApplyTo(control)), DispatcherPriority.Loaded);
            }));
    }

    public static void SetLanguage(string? language)
    {
        CurrentLanguage = string.Equals(language, English, StringComparison.OrdinalIgnoreCase)
            ? English
            : Chinese;
        ModrinthProject.PreferChineseNames = CurrentLanguage != English;
        GameInstance.UseEnglish = CurrentLanguage == English;
        RemoteVersion.UseEnglish = CurrentLanguage == English;
        InstanceExportService.UseEnglish = CurrentLanguage == English;
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

    public static void ApplyTo(DependencyObject root) => TranslateTree(root);

    public static string Translate(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return text ?? "";
        }

        return TranslateCore(text, 0);
    }

    private static string TranslateCore(string text, int depth)
    {
        if (depth > 4)
        {
            return text;
        }

        var dictionary = CurrentLanguage == English ? EnglishText : ChineseText;
        if (dictionary.TryGetValue(text, out var translated))
        {
            return translated;
        }

        if (CurrentLanguage != English)
        {
            return TryTranslateFormat(text, ChineseFormats, depth, out var chinese)
                ? chinese
                : TranslatePatternToChinese(text);
        }

        // Everything below only makes sense for Chinese source text. English
        // text (game output, mod names, player names, paths) must pass through
        // untouched.
        if (!ContainsChinese(text))
        {
            return text;
        }

        if (TryTranslateFormat(text, EnglishFormats, depth, out var formatted))
        {
            return formatted;
        }

        foreach (var (chinese, english) in ErrorPrefixes)
        {
            if (!text.StartsWith(chinese, StringComparison.Ordinal))
            {
                continue;
            }

            var rest = text[chinese.Length..];
            return english + (rest.Length == 0 ? "" : TranslateCore(rest, depth + 1));
        }

        return TranslatePattern(text);
    }

    private static bool ContainsChinese(string text)
    {
        foreach (var c in text)
        {
            if (c is >= '\u3400' and <= '\u9fff')
            {
                return true;
            }
        }

        return false;
    }

    private readonly record struct FormatRule(Regex Pattern, string Template, int PlaceholderCount);

    /// <summary>
    /// Language entries may contain positional placeholders such as
    /// <c>{0}</c>. They describe strings that are assembled in C# at runtime.
    /// This turns each entry into an anchored matcher so interpolated messages
    /// can be translated without hand-writing a regex for every one of them.
    /// Pass <paramref name="reverse"/> to build the opposite direction.
    /// </summary>
    private static List<FormatRule> BuildFormats(Dictionary<string, string> source, bool reverse)
    {
        var rules = new List<(int LiteralLength, FormatRule Rule)>();
        foreach (var (key, value) in source)
        {
            var patternSource = reverse ? value : key;
            var template = reverse ? key : value;
            var placeholders = Regex.Matches(patternSource, @"\{(\d+)\}", RegexOptions.CultureInvariant);
            if (placeholders.Count == 0)
            {
                continue;
            }

            var pattern = new StringBuilder("^");
            var last = 0;
            var count = 0;
            var skipped = false;
            foreach (Match placeholder in placeholders)
            {
                pattern.Append(Regex.Escape(patternSource[last..placeholder.Index]));
                pattern.Append("(.+?)");
                last = placeholder.Index + placeholder.Length;
                count++;
                if (!template.Contains("{" + (count - 1) + "}", StringComparison.Ordinal))
                {
                    skipped = true;
                    break;
                }
            }

            if (skipped)
            {
                continue;
            }

            pattern.Append(Regex.Escape(patternSource[last..]));
            pattern.Append('$');

            var literalLength = patternSource.Length - count * 3;
            rules.Add((literalLength, new FormatRule(
                new Regex(pattern.ToString(),
                    RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled),
                template,
                count)));
        }

        return rules
            .OrderByDescending(item => item.LiteralLength)
            .Select(item => item.Rule)
            .ToList();
    }

    private static bool TryTranslateFormat(string text, IReadOnlyList<FormatRule> rules, int depth, out string result)
    {
        foreach (var rule in rules)
        {
            var match = rule.Pattern.Match(text);
            if (!match.Success)
            {
                continue;
            }

            result = rule.Template;
            for (var i = 1; i <= rule.PlaceholderCount; i++)
            {
                var capture = match.Groups[i].Value;
                if (ContainsChinese(capture))
                {
                    capture = TranslateCore(capture, depth + 1);
                }

                result = result.Replace("{" + (i - 1) + "}", capture, StringComparison.Ordinal);
            }

            return true;
        }

        result = text;
        return false;
    }

    private static readonly (string Chinese, string English)[] ErrorPrefixes =
    {
        ("安装失败：", "Install failed: "),
        ("下载失败：", "Download failed: "),
        ("导入失败：", "Import failed: "),
        ("导出失败：", "Export failed: "),
        ("读取失败：", "Read failed: "),
        ("更新失败：", "Update failed: "),
        ("删除失败：", "Delete failed: "),
        ("保存失败：", "Save failed: "),
        ("获取失败：", "Request failed: "),
        ("查询失败：", "Lookup failed: "),
        ("搜索失败：", "Search failed: "),
        ("连接失败：", "Connection failed: "),
        ("安装前置失败：", "Failed to install prerequisite: "),
        ("读取 JAR 前置失败：", "Failed to read JAR prerequisites: "),
        ("无法打开：", "Could not open: "),
        ("无法打开目录：", "Could not open directory: "),
        ("无法打开文件夹：", "Could not open folder: "),
        ("无法打开日志：", "Could not open log: "),
        ("无法读取皮肤：", "Could not read skin: "),
        ("无法打开皮肤文件夹：", "Could not open the skin folder: "),
        ("前置检测失败：", "Prerequisite check failed: "),
        ("读取模组失败：", "Could not read mods: "),
        ("复制失败：", "Copy failed: "),
        ("登录失败：", "Sign-in failed: "),
        ("启动失败：", "Launch failed: "),
        ("备份失败：", "Backup failed: "),
        ("复制失败：", "Copy failed: "),
        ("详情加载失败：", "Failed to load details: "),
        ("启动器遇到未处理的错误：", "The launcher encountered an unhandled error: ")
    };

    private static string TranslatePatternToChinese(string text)
    {
        Match match;
        if ((match = Regex.Match(text, @"^(\d+) mods$", RegexOptions.CultureInvariant)).Success)
        {
            return $"{match.Groups[1].Value} 个模组";
        }

        if ((match = Regex.Match(text, @"^Page (\d+) / (\d+) · (\d+) total$", RegexOptions.CultureInvariant)).Success)
        {
            return $"第 {match.Groups[1].Value} / {match.Groups[2].Value} 页 · 共 {match.Groups[3].Value} 个";
        }

        if ((match = Regex.Match(text, @"^Found (\d+) projects$", RegexOptions.CultureInvariant)).Success)
        {
            return $"找到 {match.Groups[1].Value} 个项目";
        }

        if ((match = Regex.Match(text, @"^Refreshed\. (\d+) versions found\.$", RegexOptions.CultureInvariant)).Success)
        {
            return $"已刷新，共 {match.Groups[1].Value} 个版本。";
        }

        if ((match = Regex.Match(text, @"^This version requires Java (\d+)\+$", RegexOptions.CultureInvariant)).Success)
        {
            return $"此版本需要 Java {match.Groups[1].Value}+";
        }

        return text;
    }

    private static string TranslatePattern(string text)
    {
        Match match;
        if ((match = Regex.Match(text, @"^(\d+) 个模组$", RegexOptions.CultureInvariant)).Success)
        {
            return $"{match.Groups[1].Value} mods";
        }

        if ((match = Regex.Match(text, @"^第 (\d+) / (\d+) 页 · 共 (\d+) 个$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Page {match.Groups[1].Value} / {match.Groups[2].Value} · {match.Groups[3].Value} total";
        }

        if ((match = Regex.Match(text, @"^找到 (\d+) 个项目$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Found {match.Groups[1].Value} projects";
        }

        if ((match = Regex.Match(text, @"^已刷新，共 (\d+) 个版本。$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Refreshed. {match.Groups[1].Value} versions found.";
        }

        if ((match = Regex.Match(text, @"^已获取 (\d+) 个官方版本。$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Fetched {match.Groups[1].Value} official versions.";
        }

        if ((match = Regex.Match(text, @"^共 (\d+) 个模组 · (\d+) 启用 · (\d+) 禁用$", RegexOptions.CultureInvariant)).Success)
        {
            return $"{match.Groups[1].Value} mods · {match.Groups[2].Value} enabled · {match.Groups[3].Value} disabled";
        }

        if ((match = Regex.Match(text, @"^共 (\d+) 个前置 · 缺少 (\d+) 个必需$", RegexOptions.CultureInvariant)).Success)
        {
            return $"{match.Groups[1].Value} prerequisites · {match.Groups[2].Value} required missing";
        }

        if ((match = Regex.Match(text, @"^此版本需要 Java (\d+)\+$", RegexOptions.CultureInvariant)).Success)
        {
            return $"This version requires Java {match.Groups[1].Value}+";
        }

        if ((match = Regex.Match(text, @"^发现 (\d+) 个可更新模组.*$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Found {match.Groups[1].Value} updatable mods";
        }

        if ((match = Regex.Match(text, @"^还缺少 (\d+) 个必需前置.*$", RegexOptions.CultureInvariant)).Success)
        {
            return $"{match.Groups[1].Value} required prerequisites are missing";
        }

        if ((match = Regex.Match(text, @"^共 (\d+) 个版本$", RegexOptions.CultureInvariant)).Success)
        {
            return $"{match.Groups[1].Value} versions";
        }

        if ((match = Regex.Match(text, @"^共 (\d+) 个文件版本$", RegexOptions.CultureInvariant)).Success)
        {
            return $"{match.Groups[1].Value} file versions";
        }

        if ((match = Regex.Match(text, @"^可更新 v(.+)$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Update to v{match.Groups[1].Value}";
        }

        if ((match = Regex.Match(text, @"^安装 (.+)$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Install {match.Groups[1].Value}";
        }

        if ((match = Regex.Match(text, @"^存档 · (.+)$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Saves · {match.Groups[1].Value}";
        }

        if ((match = Regex.Match(text, @"^存档已备份到 (.+)$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Saves backed up to {match.Groups[1].Value}";
        }

        if ((match = Regex.Match(text, @"^文件夹 · (.+)$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Folder · {match.Groups[1].Value}";
        }

        if ((match = Regex.Match(text, @"^正在启动「(.+)」，.*$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Starting {match.Groups[1].Value}…";
        }

        if ((match = Regex.Match(text, @"^「(.+)」正在运行，.*$", RegexOptions.CultureInvariant)).Success)
        {
            return $"{match.Groups[1].Value} is running; logs opened.";
        }

        if ((match = Regex.Match(text, @"^游戏运行中 · (.+)$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Game running · {match.Groups[1].Value}";
        }

        if ((match = Regex.Match(text, @"^已手动停止游戏 · 运行 (.+)$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Stopped manually · ran for {match.Groups[1].Value}";
        }

        if ((match = Regex.Match(text, @"^游戏正常退出 · 运行 (.+)$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Game exited normally · ran for {match.Groups[1].Value}";
        }

        if ((match = Regex.Match(text, @"^已复制 (\d+) 行日志。$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Copied {match.Groups[1].Value} log lines.";
        }

        if ((match = Regex.Match(text, @"^已删除 (.+)。$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Deleted {match.Groups[1].Value}.";
        }

        if ((match = Regex.Match(text, @"^已导入 (.+)。$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Imported {match.Groups[1].Value}.";
        }

        if ((match = Regex.Match(text, @"^Minecraft (.+) 安装完成。$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Minecraft {match.Groups[1].Value} installed.";
        }

        if ((match = Regex.Match(text, @"^Minecraft (.+) 已安装。$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Minecraft {match.Groups[1].Value} is installed.";
        }

        if ((match = Regex.Match(text, @"^整合包已安装为 (.+)。$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Modpack installed as {match.Groups[1].Value}.";
        }

        if ((match = Regex.Match(text, @"^安装前置 (.+)…$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Installing prerequisite {match.Groups[1].Value}…";
        }

        if ((match = Regex.Match(text, @"^缺少 Java (\d+)\+$", RegexOptions.CultureInvariant)).Success)
        {
            return $"Java {match.Groups[1].Value}+ is missing";
        }

        return text;
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

        if (root is ItemsControl itemsControl)
        {
            HookItemsControl(itemsControl);
        }

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            TranslateTree(VisualTreeHelper.GetChild(root, i));
        }
    }

    /// <summary>
    /// WPF only raises Loaded for elements that have an instance handler, so
    /// class handlers alone never see rows created by a DataTemplate. List
    /// containers are raised through the item container generator instead,
    /// which also covers virtualized scrolling and async result pages.
    /// </summary>
    private static void HookItemsControl(ItemsControl itemsControl)
    {
        if (!HookedItemsControls.TryAdd(itemsControl, string.Empty))
        {
            return;
        }

        itemsControl.ItemContainerGenerator.StatusChanged += (_, _) =>
        {
            if (itemsControl.ItemContainerGenerator.Status != GeneratorStatus.ContainersGenerated)
            {
                return;
            }

            itemsControl.Dispatcher.BeginInvoke(
                new Action(() => TranslateTree(itemsControl)),
                DispatcherPriority.Loaded);
        };
    }

    private static void TranslateElement(DependencyObject element)
    {
        switch (element)
        {
            case Window window when !IsBound(window, Window.TitleProperty):
                window.Title = Translate(window.Title);
                break;
            case TextBlock textBlock:
                AttachDynamicText(textBlock);
                // 只翻译在 XAML 里写字面量的 TextBlock；模板里通过 TemplateBinding
                // 绑定 Content 的文本一旦被赋值就会把绑定顶掉，导致计数之类的动态内容不再刷新。
                if (!IsBound(textBlock, TextBlock.TextProperty) && IsLocallySet(textBlock, TextBlock.TextProperty))
                {
                    textBlock.Text = Translate(textBlock.Text);
                }

                TranslateInlines(textBlock.Inlines);
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
            if (!IsBound(dependencyObject, AutomationProperties.NameProperty))
            {
                var automationName = AutomationProperties.GetName(dependencyObject);
                if (!string.IsNullOrWhiteSpace(automationName))
                {
                    var localizedName = Translate(automationName);
                    if (!string.Equals(automationName, localizedName, StringComparison.Ordinal))
                    {
                        AutomationProperties.SetName(dependencyObject, localizedName);
                    }
                }
            }

            var toolTip = ToolTipService.GetToolTip(dependencyObject);
            if (toolTip is string toolTipText)
            {
                ToolTipService.SetToolTip(dependencyObject, Translate(toolTipText));
            }
        }
    }

    private static void TranslateInlines(InlineCollection inlines)
    {
        // Setting Run.Text mutates the collection's change notifications, so
        // iterate over a snapshot.
        foreach (var inline in inlines.ToList())
        {
            switch (inline)
            {
                case Run run when !IsBound(run, Run.TextProperty):
                    run.Text = Translate(run.Text);
                    break;
                case Span span:
                    TranslateInlines(span.Inlines);
                    break;
            }
        }
    }

    private static bool IsBound(DependencyObject target, DependencyProperty property)
        => BindingOperations.GetBindingExpressionBase(target, property) != null;

    private static bool IsLocallySet(DependencyObject target, DependencyProperty property)
        => DependencyPropertyHelper.GetValueSource(target, property).BaseValueSource == BaseValueSource.Local;

    private static void AttachDynamicText(TextBlock textBlock)
    {
        // C# view models produce dynamic strings (status, counts, errors). Keep
        // their binding alive and translate the effective value as it changes.
        if (DynamicTextAttached.TryAdd(textBlock, string.Empty))
        {
            var descriptor = DependencyPropertyDescriptor.FromProperty(TextBlock.TextProperty, typeof(TextBlock));
            descriptor?.AddValueChanged(textBlock, (_, _) => TranslateTextBlock(textBlock));
        }

        // Bound text may already have resolved before Loaded. Translate it once
        // here without breaking the binding.
        TranslateTextBlock(textBlock);
    }

    private static void TranslateTextBlock(TextBlock textBlock)
    {
        var current = textBlock.Text;
        var translated = Translate(current);
        if (!string.Equals(current, translated, StringComparison.Ordinal))
        {
            textBlock.SetCurrentValue(TextBlock.TextProperty, translated);
        }
    }

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
