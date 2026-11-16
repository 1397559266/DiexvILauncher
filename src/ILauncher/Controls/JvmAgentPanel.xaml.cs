using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using ILauncher.Core.Services;

namespace ILauncher.Controls;

/// <summary>
/// JVM 参数输入框下方的「快速参数」面板：搜索模组文件，一键把 -javaagent 写进参数里；
/// 打开「只显示含 Agent」时会扫描已启用模组的 manifest，列出真正带 Premain-Class 的 jar。
/// </summary>
public partial class JvmAgentPanel : UserControl, INotifyPropertyChanged
{
    public static readonly DependencyProperty TargetTextProperty = DependencyProperty.Register(
        nameof(TargetText), typeof(string), typeof(JvmAgentPanel),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault));

    public static readonly DependencyProperty ModsDirectoryProperty = DependencyProperty.Register(
        nameof(ModsDirectory), typeof(string), typeof(JvmAgentPanel),
        new PropertyMetadata("", OnModsDirectoryChanged));

    private readonly List<AgentCandidate> _candidates = new();
    private readonly Dictionary<string, JarAgentInfo> _scanned = new(StringComparer.OrdinalIgnoreCase);
    private string _searchText = "";
    private string _statusText = "搜索模组文件，点击「加入」即可追加 -javaagent 参数。";
    private bool _onlyAgents;
    private bool _scanning;

    public JvmAgentPanel()
    {
        InitializeComponent();
        // 不设 DataContext：外层绑定（TargetText/ModsDirectory）要用父级 ViewModel；
        // 内部模板通过 RelativeSource 绑定到本控件。
        Loaded += (_, _) => Reload();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AgentCandidate> Items { get; } = new();

    /// <summary>列表为空时给个提示，避免看起来像卡住了。</summary>
    public bool IsEmpty => Items.Count == 0;

    /// <summary>要追加参数的输入框内容（与 ExtraJvmArgs 双向绑定）。</summary>
    public string TargetText
    {
        get => (string)GetValue(TargetTextProperty);
        set => SetValue(TargetTextProperty, value);
    }

    /// <summary>当前实例的 mods 目录；为空时面板提示先选择版本。</summary>
    public string ModsDirectory
    {
        get => (string)GetValue(ModsDirectoryProperty);
        set => SetValue(ModsDirectoryProperty, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value)
            {
                return;
            }

            _searchText = value ?? "";
            Raise();
            ApplyFilter();
        }
    }

    /// <summary>开启后扫描 manifest，只保留含 Premain-Class 的模组。</summary>
    public bool OnlyAgents
    {
        get => _onlyAgents;
        set
        {
            if (_onlyAgents == value)
            {
                return;
            }

            _onlyAgents = value;
            Raise();
            if (value)
            {
                _ = ScanAsync();
            }
            else
            {
                ApplyFilter();
            }
        }
    }

    public string StatusText
    {
        get => _statusText;
        private set
        {
            _statusText = value;
            Raise();
        }
    }

    private static void OnModsDirectoryChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is JvmAgentPanel panel)
        {
            panel.Reload();
        }
    }

    private void Reload()
    {
        _candidates.Clear();
        _scanned.Clear();
        Items.Clear();

        var directory = ModsDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            StatusText = "先选择一个游戏版本，这里会列出它的模组文件。";
            return;
        }

        foreach (var path in JarAgentScanner.ListJars(directory))
        {
            _candidates.Add(new AgentCandidate(path));
        }

        RefreshBadges();
        ApplyFilter();
        StatusText = _onlyAgents
            ? StatusText
            : $"共 {_candidates.Count} 个已启用模组；打开「只显示含 Agent」可扫描 Premain-Class。";
    }

    private async Task ScanAsync()
    {
        if (_scanning)
        {
            return;
        }

        _scanning = true;
        StatusText = "正在扫描模组里的 Premain-Class…";
        try
        {
            var paths = _candidates.Select(c => c.FilePath).ToList();
            var progress = new Progress<int>(percent =>
                StatusText = $"正在扫描模组里的 Premain-Class… {percent}%");
            var found = await Task.Run(() => JarAgentScanner.Scan(paths, progress));

            foreach (var info in found)
            {
                _scanned[info.FilePath] = info;
            }

            RefreshBadges();
            ApplyFilter();
            StatusText = found.Count == 0
                ? $"扫描完成：{paths.Count} 个模组里没有发现 Premain-Class。"
                : $"扫描完成：{found.Count} 个模组带 Agent 入口，可直接加入参数。";
        }
        catch (Exception ex)
        {
            StatusText = $"扫描失败：{ex.Message}";
        }
        finally
        {
            _scanning = false;
        }
    }

    private void RefreshBadges()
    {
        foreach (var candidate in _candidates)
        {
            if (_scanned.TryGetValue(candidate.FilePath, out var info))
            {
                candidate.HasAgent = true;
                candidate.AgentBadge = info.PremainClass is { Length: > 0 } premain
                    ? $"Premain · {premain}"
                    : info.AgentClass is { Length: > 0 } agent
                        ? $"Agent · {agent}"
                        : "Agent";
            }
        }
    }

    private void ApplyFilter()
    {
        var keyword = SearchText.Trim();
        var filtered = _candidates.Where(candidate =>
            (!_onlyAgents || candidate.HasAgent) &&
            (keyword.Length == 0 ||
             candidate.FileName.Contains(keyword, StringComparison.OrdinalIgnoreCase)));

        Items.Clear();
        foreach (var candidate in filtered)
        {
            Items.Add(candidate);
        }

        Raise(nameof(IsEmpty));
    }

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: AgentCandidate candidate })
        {
            return;
        }

        var argument = candidate.Argument;
        var current = TargetText ?? "";
        if (current.Contains(argument, StringComparison.OrdinalIgnoreCase))
        {
            StatusText = $"{candidate.FileName} 的参数已经在上面了。";
            return;
        }

        // -javaagent 只对声明了 Premain-Class 的 jar 有效，Agent-Class 需要动态 attach。
        if (!_scanned.ContainsKey(candidate.FilePath))
        {
            var info = JarAgentScanner.Inspect(candidate.FilePath);
            if (info.HasAgent)
            {
                _scanned[candidate.FilePath] = info;
                candidate.HasAgent = true;
                candidate.AgentBadge = info.PremainClass is { Length: > 0 } premain
                    ? $"Premain · {premain}"
                    : $"Agent · {info.AgentClass}";
            }
        }

        var trimmed = current.TrimEnd();
        SetCurrentValue(TargetTextProperty, trimmed.Length == 0 ? argument : $"{trimmed} {argument}");
        StatusText = candidate.HasAgent
            ? $"已加入 {candidate.FileName}。"
            : $"已加入 {candidate.FileName}，但它没有 Premain-Class，-javaagent 启动时可能报错。";
    }

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>面板里的一行候选模组。</summary>
public sealed class AgentCandidate : INotifyPropertyChanged
{
    private bool _hasAgent;
    private string _agentBadge = "";

    public AgentCandidate(string filePath)
    {
        FilePath = filePath;
        FileName = Path.GetFileName(filePath);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FilePath { get; }

    public string FileName { get; }

    public bool HasAgent
    {
        get => _hasAgent;
        set
        {
            if (_hasAgent == value)
            {
                return;
            }

            _hasAgent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasAgent)));
        }
    }

    public string AgentBadge
    {
        get => _agentBadge;
        set
        {
            if (_agentBadge == value)
            {
                return;
            }

            _agentBadge = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AgentBadge)));
        }
    }

    /// <summary>-javaagent:"绝对路径"（路径含空格也安全）。</summary>
    public string Argument => $"-javaagent:\"{FilePath}\"";
}
