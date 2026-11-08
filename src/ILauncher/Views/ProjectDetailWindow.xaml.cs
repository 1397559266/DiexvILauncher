using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using ILauncher.Core.Services;

namespace ILauncher.Views;

public partial class ProjectDetailWindow : Window, INotifyPropertyChanged
{
    private readonly ModrinthService _modrinth;
    private string _statusText = "正在加载详情…";
    private string _bodyText = "";
    private string _linksText = "";
    private bool _hasGallery;
    private bool _hasLinks;

    public ProjectDetailWindow(ModrinthProject project, ModrinthService modrinth)
    {
        Project = project;
        _modrinth = modrinth;
        InitializeComponent();
        DataContext = this;
        Loaded += OnLoaded;
    }

    public ModrinthProject Project { get; }
    public ObservableCollection<ModrinthGalleryImage> Gallery { get; } = new();

    public string StatusText
    {
        get => _statusText;
        private set => Set(ref _statusText, value);
    }

    public string BodyText
    {
        get => _bodyText;
        private set => Set(ref _bodyText, value);
    }

    public string LinksText
    {
        get => _linksText;
        private set => Set(ref _linksText, value);
    }

    public bool HasGallery
    {
        get => _hasGallery;
        private set => Set(ref _hasGallery, value);
    }

    public bool HasLinks
    {
        get => _hasLinks;
        private set => Set(ref _hasLinks, value);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        Loaded -= OnLoaded;
        try
        {
            var details = await _modrinth.GetProjectDetailsAsync(Project.Id);
            if (details == null)
            {
                StatusText = "无法加载详情";
                return;
            }

            foreach (var image in details.Gallery)
            {
                Gallery.Add(image);
            }

            HasGallery = Gallery.Count > 0;
            BodyText = ToPlainText(details.Body);
            var links = new[]
            {
                details.SourceUrl,
                details.IssuesUrl,
                details.WikiUrl
            }.Where(link => !string.IsNullOrWhiteSpace(link)).ToArray();
            LinksText = string.Join("\n", links!);
            HasLinks = links.Length > 0;
            StatusText = "";
        }
        catch (Exception ex)
        {
            StatusText = $"详情加载失败：{ex.Message}";
        }
    }

    private static string ToPlainText(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            return "暂无介绍";
        }

        var text = Regex.Replace(markdown, @"!\[[^\]]*\]\([^\)]+\)", "");
        text = Regex.Replace(text, @"\[([^\]]+)\]\([^\)]+\)", "$1");
        text = Regex.Replace(text, @"<[^>]+>", "");
        text = Regex.Replace(text, @"^#{1,6}\s*", "", RegexOptions.Multiline);
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        return text.Trim();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        return true;
    }
}
