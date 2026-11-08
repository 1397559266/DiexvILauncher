using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using ILauncher.Core;
using ILauncher.Core.Models;
using ILauncher.Core.Services;
using ILauncher.Models;

namespace ILauncher.ViewModels;

public sealed class LaunchLogViewModel : ObservableObject
{
    private static readonly Brush StatusIdleBrush = CreateBrush(0x8A, 0x97, 0xA8);
    private static readonly Brush StatusBusyBrush = CreateBrush(0x13, 0x70, 0xF3);
    private static readonly Brush StatusRunningBrush = CreateBrush(0x16, 0xA3, 0x4A);
    private static readonly Brush StatusFailedBrush = CreateBrush(0xE5, 0x48, 0x4D);
    private static readonly Brush StatusStoppedBrush = CreateBrush(0xE8, 0xA2, 0x3D);

    private readonly LaunchService _launch;
    private readonly SettingsService _settings;
    private readonly Action<string, bool> _toast;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _clockTimer;
    private readonly DispatcherTimer _searchTimer;

    private GameInstance? _instance;
    private CancellationTokenSource? _cts;
    private DateTime _startedAt;
    private string _activeFilter = "all";
    private string _searchLower = "";
    private bool _isRunning;
    private bool _isFinished;
    private bool _autoScroll = true;
    private string _statusText = "等待启动";
    private Brush _statusBrush = StatusIdleBrush;
    private string _statusDetail = "点击「测试游戏」启动游戏后，这里会实时显示全部输出。";
    private string _elapsedText = "00:00";
    private string _pidText = "—";
    private string _logFilePath = "";
    private string _searchText = "";
    private bool _testMode = true;
    private int _allCount;
    private int _infoCount;
    private int _warnCount;
    private int _errorCount;
    private int _debugCount;
    private TimeSpan _finalElapsed;
    private string? _serverAddress;

    public LaunchLogViewModel(LaunchService launch, SettingsService settings, Action<string, bool> toast)
    {
        _launch = launch;
        _settings = settings;
        _toast = toast;

        LogsView = CollectionViewSource.GetDefaultView(Logs);
        LogsView.Filter = FilterEntry;

        Steps.Add(new LaunchStep { Title = "检查环境", Index = 0 });
        Steps.Add(new LaunchStep { Title = "构建参数", Index = 1 });
        Steps.Add(new LaunchStep { Title = "启动进程", Index = 2 });
        Steps.Add(new LaunchStep { Title = "游戏运行", Index = 3, IsLast = true });

        LevelFilters.Add(new FilterItem { Title = "全部", Key = "all", IsSelected = true, DotBrush = CreateBrush(0x8A, 0x97, 0xA8) });
        LevelFilters.Add(new FilterItem { Title = "信息", Key = "info", DotBrush = CreateBrush(0x3E, 0x6B, 0x9E) });
        LevelFilters.Add(new FilterItem { Title = "警告", Key = "warn", DotBrush = CreateBrush(0xC2, 0x82, 0x1E) });
        LevelFilters.Add(new FilterItem { Title = "错误", Key = "error", DotBrush = CreateBrush(0xD9, 0x3B, 0x42) });
        LevelFilters.Add(new FilterItem { Title = "调试", Key = "debug", DotBrush = CreateBrush(0x7C, 0x8A, 0x9E) });

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(70) };
        _flushTimer.Tick += (_, _) => FlushPending();

        _clockTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _clockTimer.Tick += (_, _) =>
        {
            if (IsRunning)
            {
                ElapsedText = FormatDuration(DateTime.Now - _startedAt);
            }
        };

        _searchTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(220) };
        _searchTimer.Tick += (_, _) =>
        {
            _searchTimer.Stop();
            _searchLower = SearchText.Trim().ToLowerInvariant();
            LogsView.Refresh();
            OnPropertyChanged(nameof(IsEmpty));
        };

        _launch.LogReceived += OnLogReceived;
        _launch.StageChanged += OnStageChanged;

        StopCommand = new RelayCommand(Stop, () => IsRunning);
        ClearCommand = new RelayCommand(Clear, () => Logs.Count > 0 || _allCount > 0);
        CopyCommand = new RelayCommand(CopyAll, () => Logs.Count > 0);
        OpenLogFileCommand = new RelayCommand(OpenLogFile, () => File.Exists(LogFilePath));
        OpenLogFolderCommand = new RelayCommand(OpenLogFolder, () => !string.IsNullOrWhiteSpace(LogFilePath));
        RetryCommand = new RelayCommand(() =>
        {
            if (_instance != null)
            {
                Start(_instance, _testMode, _serverAddress);
            }
        }, () => !IsRunning && _instance != null);
        SetFilterCommand = new RelayCommand(SetFilter);
    }

    public ObservableCollection<LogEntry> Logs { get; } = new();

    public ICollectionView LogsView { get; }

    public ObservableCollection<LaunchStep> Steps { get; } = new();

    public ObservableCollection<FilterItem> LevelFilters { get; } = new();

    public RelayCommand StopCommand { get; }
    public RelayCommand ClearCommand { get; }
    public RelayCommand CopyCommand { get; }
    public RelayCommand OpenLogFileCommand { get; }
    public RelayCommand OpenLogFolderCommand { get; }
    public RelayCommand RetryCommand { get; }
    public RelayCommand SetFilterCommand { get; }

    public GameInstance? Instance
    {
        get => _instance;
        private set
        {
            if (SetProperty(ref _instance, value))
            {
                OnPropertyChanged(nameof(InstanceId));
                OnPropertyChanged(nameof(InstanceSubtitle));
                OnPropertyChanged(nameof(SessionTitle));
                OnPropertyChanged(nameof(HeaderTitle));
            }
        }
    }

    public string InstanceId => Instance?.Id ?? "未选择版本";

    public string InstanceSubtitle => Instance?.Subtitle ?? "";

    public string SessionTitle => IsTestMode ? "测试游戏" : "启动游戏";

    public string HeaderTitle => IsTestMode ? "测试游戏 · 启动日志" : "启动日志";

    public bool IsTestMode
    {
        get => _testMode;
        private set
        {
            if (SetProperty(ref _testMode, value))
            {
                OnPropertyChanged(nameof(SessionTitle));
                OnPropertyChanged(nameof(HeaderTitle));
            }
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
            {
                OnPropertyChanged(nameof(CanStop));
                OnPropertyChanged(nameof(IsFinished));
                OnPropertyChanged(nameof(ShowEmptyState));
                OnPropertyChanged(nameof(CaptureStateText));
                StopCommand.RaiseCanExecuteChanged();
                RetryCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool CanStop => IsRunning;

    public bool IsFinished
    {
        get => _isFinished;
        private set => SetProperty(ref _isFinished, value);
    }

    public bool ShowEmptyState => Logs.Count == 0 && !IsRunning;

    public bool AutoScroll
    {
        get => _autoScroll;
        set => SetProperty(ref _autoScroll, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public Brush StatusBrush
    {
        get => _statusBrush;
        private set => SetProperty(ref _statusBrush, value);
    }

    public string StatusDetail
    {
        get => _statusDetail;
        private set => SetProperty(ref _statusDetail, value);
    }

    public string ElapsedText
    {
        get => _elapsedText;
        private set => SetProperty(ref _elapsedText, value);
    }

    public string PidText
    {
        get => _pidText;
        private set => SetProperty(ref _pidText, value);
    }

    public string LogFilePath
    {
        get => _logFilePath;
        private set
        {
            if (SetProperty(ref _logFilePath, value))
            {
                OnPropertyChanged(nameof(HasLogFile));
                OnPropertyChanged(nameof(LogPathDisplay));
                OpenLogFileCommand.RaiseCanExecuteChanged();
                OpenLogFolderCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public bool HasLogFile => !string.IsNullOrWhiteSpace(LogFilePath);

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                _searchTimer.Stop();
                _searchTimer.Start();
            }
        }
    }

    public bool IsEmpty => Logs.Count == 0;

    public string LineCountText => $"{_allCount:N0} 行";

    public string ErrorCountText => $"{_errorCount:N0}";

    public string WarnCountText => $"{_warnCount:N0}";

    public string LogPathDisplay => string.IsNullOrWhiteSpace(LogFilePath) ? "启动后自动创建日志文件" : LogFilePath;

    public string CaptureStateText => IsRunning ? "实时捕获 stdout + stderr" : "日志已保存到本地";

    public EventHandler? ScrollToEndRequested;

    public void Start(GameInstance instance, bool testMode = true, string? serverAddress = null)
    {
        if (IsRunning)
        {
            _toast("游戏已经在运行中。", true);
            return;
        }

        Instance = instance;
        IsTestMode = testMode;
        _serverAddress = serverAddress;
        ClearLogs();
        IsFinished = false;
        IsRunning = true;
        _startedAt = DateTime.Now;
        _finalElapsed = TimeSpan.Zero;
        ElapsedText = "00:00";
        PidText = "—";
        LogFilePath = "";
        StatusText = "准备中";
        StatusBrush = StatusBusyBrush;
        StatusDetail = testMode ? "正在启动测试游戏，请稍候…" : "正在启动游戏，请稍候…";
        foreach (var step in Steps)
        {
            step.State = LaunchStepState.Pending;
        }

        Steps[0].State = LaunchStepState.Active;
        _flushTimer.Start();
        _clockTimer.Start();

        var overrides = _settings.GetInstanceSettings(instance.Id);
        var memory = overrides.MemoryMb ?? _settings.Settings.MemoryMb;
        var javaPath = string.IsNullOrWhiteSpace(overrides.JavaPath) ? _settings.Settings.JavaPath : overrides.JavaPath;
        var width = overrides.Width ?? _settings.Settings.WindowWidth;
        var height = overrides.Height ?? _settings.Settings.WindowHeight;

        var isMicrosoft = string.Equals(_settings.Settings.AccountType, "微软账户", StringComparison.Ordinal) &&
                         !string.IsNullOrWhiteSpace(_settings.Settings.MicrosoftAccessToken);

        var request = new LaunchRequest
        {
            Instance = instance,
            PlayerName = _settings.ResolvePlayerName(),
            MemoryMb = memory,
            JavaPath = string.IsNullOrWhiteSpace(javaPath) ? null : javaPath,
            Width = width,
            Height = height,
            TestMode = testMode,
            ServerAddress = serverAddress,
            AccessToken = isMicrosoft ? _settings.Settings.MicrosoftAccessToken : null,
            UserUuid = isMicrosoft ? _settings.Settings.MicrosoftUuid : null,
            UserType = isMicrosoft ? "msa" : null,
            LogDirectory = AppPaths.LogsDirectory
        };

        _cts = new CancellationTokenSource();
        _ = RunAsync(request, testMode);
    }

    private async Task RunAsync(LaunchRequest request, bool testMode)
    {
        LaunchResult? result = null;
        try
        {
            result = await _launch.RunAsync(request, _cts?.Token ?? CancellationToken.None);
        }
        catch (Exception ex)
        {
            StatusText = "启动失败";
            StatusBrush = StatusFailedBrush;
            StatusDetail = ex.Message;
            _pending.Enqueue(LogEntryParser.Parse($"[启动器] 启动失败：{ex.Message}", DateTime.Now, true));
        }
        finally
        {
            IsRunning = false;
            IsFinished = true;
            _flushTimer.Stop();
            FlushPending();
            _clockTimer.Stop();
            _finalElapsed = _startedAt == default ? TimeSpan.Zero : DateTime.Now - _startedAt;
            ElapsedText = FormatDuration(_finalElapsed);

            if (result is { Started: false })
            {
                StatusText = "启动失败";
                StatusBrush = StatusFailedBrush;
                StatusDetail = result.FailureReason ?? "未能启动游戏。";
                MarkFailedStep();
                _toast($"启动失败：{result.FailureReason}", true);
            }
            else if (result is { Stopped: true })
            {
                StatusText = "已停止";
                StatusBrush = StatusStoppedBrush;
                StatusDetail = $"已手动停止游戏 · 运行 {ElapsedText}";
                _toast("游戏已停止。", false);
            }
            else if (result is { Success: true })
            {
                StatusText = "已退出";
                StatusBrush = StatusStoppedBrush;
                StatusDetail = $"游戏正常退出 · 运行 {ElapsedText}";
                _toast("游戏已退出。", false);
            }
            else if (result != null)
            {
                StatusText = "启动失败";
                StatusBrush = StatusFailedBrush;
                StatusDetail = $"游戏异常退出，退出码 {result.ExitCode} · 运行 {ElapsedText}";
                MarkFailedStep();
                _toast($"游戏异常退出（退出码 {result.ExitCode}），请在日志中查看原因。", true);
            }

            if (result is { Success: false, Stopped: false })
            {
                AppendDiagnostics();
            }

            OnPropertyChanged(nameof(ShowEmptyState));
            OnPropertyChanged(nameof(LineCountText));
            OnPropertyChanged(nameof(ErrorCountText));
            OnPropertyChanged(nameof(WarnCountText));
            RetryCommand.RaiseCanExecuteChanged();
        }
    }

    private void AppendDiagnostics()
    {
        try
        {
            var tips = CrashAnalyzer.Analyze(Logs.Select(entry => entry.Message));
            if (tips.Count == 0)
            {
                return;
            }

            _pending.Enqueue(LogEntryParser.Parse("[诊断] 启动器检测到可能的问题：", DateTime.Now, true));
            foreach (var tip in tips)
            {
                _pending.Enqueue(LogEntryParser.Parse($"[诊断] {tip}", DateTime.Now, true));
            }

            FlushPending();
        }
        catch
        {
            // Diagnostics are a convenience and must not hide the original failure.
        }
    }

    private void Stop()
    {        StatusText = "停止中";
        StatusBrush = StatusStoppedBrush;
        StatusDetail = "正在结束游戏进程…";
        _launch.Stop();
    }

    private void Clear()
    {
        ClearLogs();
        StatusDetail = IsRunning ? "日志已清空，游戏仍在运行。" : "日志已清空。";
        OnPropertyChanged(nameof(ShowEmptyState));
    }

    private void ClearLogs()
    {
        while (_pending.TryDequeue(out _))
        {
        }

        Logs.Clear();
        _allCount = _infoCount = _warnCount = _errorCount = _debugCount = 0;
        UpdateFilterCounts();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowEmptyState));
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(ErrorCountText));
        OnPropertyChanged(nameof(WarnCountText));
        ClearCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
    }

    private void OnLogReceived(object? sender, LaunchLogEventArgs e)
    {
        var entry = LogEntryParser.Parse(e.Line, e.Timestamp, e.IsLauncherLine);
        _pending.Enqueue(entry);
    }

    private void FlushPending()
    {
        if (_pending.IsEmpty)
        {
            return;
        }

        var added = 0;
        while (added < 4000 && _pending.TryDequeue(out var entry))
        {
            Logs.Add(entry);
            added++;
            _allCount++;
            switch (entry.Kind)
            {
                case LogLevelKind.Error:
                    _errorCount++;
                    break;
                case LogLevelKind.Warn:
                    _warnCount++;
                    break;
                case LogLevelKind.Debug:
                case LogLevelKind.Trace:
                    _debugCount++;
                    break;
                default:
                    _infoCount++;
                    break;
            }
        }

        if (added == 0)
        {
            return;
        }

        // Keep memory bounded for very long sessions.
        if (Logs.Count > 50000)
        {
            var remove = Logs.Count - 40000;
            for (var i = 0; i < remove; i++)
            {
                Logs.RemoveAt(0);
            }

            _allCount = Math.Max(_allCount, Logs.Count);
        }

        UpdateFilterCounts();
        OnPropertyChanged(nameof(LineCountText));
        OnPropertyChanged(nameof(ErrorCountText));
        OnPropertyChanged(nameof(WarnCountText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(ShowEmptyState));
        ClearCommand.RaiseCanExecuteChanged();
        CopyCommand.RaiseCanExecuteChanged();
        ScrollToEndRequested?.Invoke(this, EventArgs.Empty);
    }

    private void UpdateFilterCounts()
    {
        LevelFilters[0].Count = _allCount;
        LevelFilters[1].Count = _infoCount;
        LevelFilters[2].Count = _warnCount;
        LevelFilters[3].Count = _errorCount;
        LevelFilters[4].Count = _debugCount;
    }

    private bool FilterEntry(object item)
    {
        if (item is not LogEntry entry)
        {
            return false;
        }

        if (!string.IsNullOrEmpty(_searchLower) &&
            !entry.SearchText.Contains(_searchLower, StringComparison.Ordinal))
        {
            return false;
        }

        return _activeFilter switch
        {
            "info" => entry.Kind is LogLevelKind.Info or LogLevelKind.Launcher,
            "warn" => entry.Kind == LogLevelKind.Warn,
            "error" => entry.Kind == LogLevelKind.Error,
            "debug" => entry.Kind is LogLevelKind.Debug or LogLevelKind.Trace,
            _ => true
        };
    }

    private void SetFilter(object? parameter)
    {
        var key = parameter as string ?? "all";
        if (_activeFilter == key)
        {
            return;
        }

        _activeFilter = key;
        foreach (var filter in LevelFilters)
        {
            filter.IsSelected = string.Equals(filter.Key, key, StringComparison.Ordinal);
        }

        LogsView.Refresh();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void OnStageChanged(object? sender, LaunchStageEventArgs e)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null)
        {
            return;
        }

        dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            if (!IsRunning)
            {
                return;
            }

            switch (e.Stage)
            {
                case LaunchStage.Checking:
                    Steps[0].State = LaunchStepState.Active;
                    StatusText = "准备中";
                    StatusBrush = StatusBusyBrush;
                    StatusDetail = e.Message;
                    break;
                case LaunchStage.Building:
                    CompleteStep(0);
                    Steps[1].State = LaunchStepState.Active;
                    StatusText = "构建参数";
                    StatusBrush = StatusBusyBrush;
                    StatusDetail = e.Message;
                    break;
                case LaunchStage.Starting:
                    CompleteStep(0);
                    CompleteStep(1);
                    Steps[2].State = LaunchStepState.Active;
                    StatusText = "启动中";
                    StatusBrush = StatusBusyBrush;
                    StatusDetail = e.Message;
                    PidText = _launch.ProcessId is { } pid ? $"PID {pid}" : "—";
                    LogFilePath = _launch.CurrentLogFile ?? "";
                    break;
                case LaunchStage.WaitingWindow:
                    CompleteStep(0);
                    CompleteStep(1);
                    CompleteStep(2);
                    Steps[3].State = LaunchStepState.Active;
                    StatusText = "等待窗口";
                    StatusBrush = StatusBusyBrush;
                    StatusDetail = e.Message;
                    break;
                case LaunchStage.Running:
                    CompleteStep(0);
                    CompleteStep(1);
                    CompleteStep(2);
                    CompleteStep(3);
                    StatusText = "运行中";
                    StatusBrush = StatusRunningBrush;
                    StatusDetail = "游戏窗口已就绪，正在实时捕获全部日志。";
                    PidText = _launch.ProcessId is { } runningPid ? $"PID {runningPid}" : PidText;
                    LogFilePath = _launch.CurrentLogFile ?? LogFilePath;
                    break;
                case LaunchStage.Stopping:
                    StatusText = "停止中";
                    StatusBrush = StatusStoppedBrush;
                    StatusDetail = e.Message;
                    break;
                case LaunchStage.Exited:
                    CompleteStep(0);
                    CompleteStep(1);
                    CompleteStep(2);
                    CompleteStep(3);
                    StatusText = "已退出";
                    StatusBrush = StatusStoppedBrush;
                    StatusDetail = e.Message;
                    break;
                case LaunchStage.Failed:
                    MarkFailedStep();
                    StatusText = "启动失败";
                    StatusBrush = StatusFailedBrush;
                    StatusDetail = e.Message;
                    break;
            }
        }));
    }

    private void CompleteStep(int index)
    {
        if (index >= 0 && index < Steps.Count && Steps[index].State != LaunchStepState.Failed)
        {
            Steps[index].State = LaunchStepState.Done;
        }
    }

    private void MarkFailedStep()
    {
        var active = Steps.FirstOrDefault(s => s.State == LaunchStepState.Active);
        if (active != null)
        {
            active.State = LaunchStepState.Failed;
        }
        else if (Steps.Count > 0)
        {
            Steps[^1].State = LaunchStepState.Failed;
        }
    }

    private void CopyAll()
    {
        try
        {
            var builder = new StringBuilder();
            var count = 0;
            foreach (var item in LogsView)
            {
                if (item is LogEntry entry)
                {
                    builder.AppendLine(entry.Message);
                    count++;
                    if (builder.Length > 4_000_000)
                    {
                        break;
                    }
                }
            }

            Clipboard.SetText(builder.ToString());
            _toast($"已复制 {count} 行日志。", false);
        }
        catch (Exception ex)
        {
            _toast($"复制失败：{ex.Message}", true);
        }
    }

    private void OpenLogFile()
    {
        if (!File.Exists(LogFilePath))
        {
            _toast("日志文件尚未生成。", true);
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(LogFilePath) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast($"无法打开日志：{ex.Message}", true);
        }
    }

    private void OpenLogFolder()
    {
        try
        {
            var path = File.Exists(LogFilePath) ? LogFilePath : AppPaths.LogsDirectory;
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _toast($"无法打开文件夹：{ex.Message}", true);
        }
    }

    private static string FormatDuration(TimeSpan span)
        => span.TotalHours >= 1 ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}" : $"{span.Minutes:00}:{span.Seconds:00}";

    private static Brush CreateBrush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
