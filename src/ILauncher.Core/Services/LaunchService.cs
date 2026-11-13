using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ILauncher.Core.Minecraft;
using ILauncher.Core.Models;

namespace ILauncher.Core.Services;

public sealed class LaunchException : Exception
{
    public LaunchException(string message) : base(message)
    {
    }
}

public sealed partial class LaunchService
{
    private readonly JavaService _java;
    private static readonly HttpClient Http = CreateHttpClient();

    private readonly object _logLock = new();
    private StreamWriter? _logWriter;
    private Process? _process;
    private CancellationTokenSource? _internalCts;
    private bool _windowDetected;
    private bool _stopRequested;
    private string? _logFilePath;
    private int _unknownPlaceholders;

    public LaunchService(JavaService java)
    {
        _java = java;
    }

    public event EventHandler<LaunchLogEventArgs>? LogReceived;

    public event EventHandler<LaunchStageEventArgs>? StageChanged;

    public LaunchStage Stage { get; private set; } = LaunchStage.Idle;

    public bool IsRunning => _process is { HasExited: false };

    public int? ProcessId => _process is { HasExited: false } process ? process.Id : null;

    public LaunchPlan? CurrentPlan { get; private set; }

    public string? CurrentLogFile => _logFilePath;

    public async Task<LaunchResult> RunAsync(LaunchRequest request, CancellationToken cancellationToken = default)
    {        if (IsRunning)
        {
            throw new LaunchException("已有游戏进程正在运行。");
        }

        var stopwatch = Stopwatch.StartNew();
        _windowDetected = false;
        _stopRequested = false;
        _unknownPlaceholders = 0;
        _internalCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = _internalCts.Token;

        try
        {
            SetStage(LaunchStage.Checking, "检查游戏文件与运行环境…");
            var plan = await PrepareAsync(request, token);
            CurrentPlan = plan;
            _logFilePath = plan.LogFilePath;

            SetStage(LaunchStage.Starting, "正在启动 Java 进程…");
            var result = await StartProcessAsync(plan, request, stopwatch, token);
            return result;
        }
        catch (OperationCanceledException)
        {
            SetStage(LaunchStage.Exited, "启动已取消");
            Emit("[启动器] 启动已取消。", launcher: true);
            return new LaunchResult
            {
                Started = false,
                Success = false,
                FailureReason = "启动已取消",
                Duration = stopwatch.Elapsed,
                Plan = CurrentPlan
            };
        }
        catch (Exception ex)
        {
            SetStage(LaunchStage.Failed, "启动失败");
            Emit($"[启动器] 启动失败：{ex.Message}", launcher: true, error: true);
            return new LaunchResult
            {
                Started = false,
                Success = false,
                FailureReason = ex.Message,
                Duration = stopwatch.Elapsed,
                Plan = CurrentPlan
            };
        }
        finally
        {
            CloseLogFile();
            _process?.Dispose();
            _process = null;
            _internalCts?.Dispose();
            _internalCts = null;
        }
    }

    /// <summary>Builds a launch plan (and downloads missing files) without starting the game.</summary>
    public async Task<LaunchPlan> CreatePlanAsync(LaunchRequest request, CancellationToken cancellationToken = default)
    {
        if (IsRunning)
        {
            throw new LaunchException("已有游戏进程正在运行，无法生成启动脚本。");
        }

        _unknownPlaceholders = 0;
        try
        {
            SetStage(LaunchStage.Checking, "检查游戏文件与运行环境…");
            var plan = await PrepareAsync(request, cancellationToken);
            CurrentPlan = plan;
            SetStage(LaunchStage.Exited, "启动脚本已生成");
            return plan;
        }
        finally
        {
            CloseLogFile();
        }
    }

    public void Stop()
    {
        if (_process is not { HasExited: false } process)
        {
            return;
        }

        SetStage(LaunchStage.Stopping, "正在停止游戏…");
        _stopRequested = true;
        Emit("[启动器] 正在结束游戏进程…", launcher: true);
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex)
        {
            Emit($"[启动器] 结束进程失败：{ex.Message}", launcher: true, error: true);
        }
    }

    private async Task<LaunchPlan> PrepareAsync(LaunchRequest request, CancellationToken cancellationToken)
    {
        var instance = request.Instance;
        if (!File.Exists(instance.JsonPath))
        {
            throw new LaunchException($"版本文件不存在：{instance.JsonPath}");
        }

        var metadata = VersionResolver.Load(instance.RootDirectory, instance.Id);
        var features = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase)
        {
            ["is_demo_user"] = false,
            ["has_custom_resolution"] = request.Width is > 0 && request.Height is > 0,
            ["has_quick_plays_support"] = false,
            ["is_quick_play_singleplayer"] = false,
            ["is_quick_play_multiplayer"] = false,
            ["is_quick_play_realms"] = false
        };

        var logDirectory = request.LogDirectory ?? AppPaths.LogsDirectory;
        Directory.CreateDirectory(logDirectory);
        _logFilePath = Path.Combine(logDirectory,
            $"{(request.TestMode ? "test" : "launch")}-{AppPaths.SanitizeFileName(instance.Id)}-{DateTime.Now:yyyyMMdd-HHmmss-fff}.log");
        OpenLogFile(_logFilePath);

        Emit("════════════════════════════════════════════════", launcher: true);
        Emit($"[启动器] ILauncher 1.1.0 · {(request.TestMode ? "测试游戏" : "启动游戏")}", launcher: true);
        Emit($"[启动器] 版本：{instance.Id}   ({instance.Subtitle})", launcher: true);
        Emit($"[启动器] 时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}", launcher: true);
        Emit($"[启动器] 日志文件：{_logFilePath}", launcher: true);
        Emit("════════════════════════════════════════════════", launcher: true);

        var java = await _java.ResolveAsync(instance.RequiredJavaMajor, request.JavaPath, cancellationToken);
        if (java == null)
        {
            throw new LaunchException($"未找到 Java {instance.RequiredJavaMajor} 或更高版本，请在设置中指定 java.exe。");
        }

        if (java.Major < instance.RequiredJavaMajor)
        {
            throw new LaunchException(
                $"当前 Java 版本过低：需要 Java {instance.RequiredJavaMajor}+，检测到 Java {java.Major}（{java.Path}）。");
        }

        Emit($"[启动器] Java：{java.Version}（{java.Path}）", launcher: true);

        var gameDirectory = string.IsNullOrWhiteSpace(request.GameDirectory)
            ? instance.GameDirectory
            : request.GameDirectory!;
        Directory.CreateDirectory(gameDirectory);

        var clientJar = VersionResolver.ResolveClientJar(instance, metadata);
        if (clientJar == null && metadata.Client?.Url is { Length: > 0 } clientUrl)
        {
            var target = Path.Combine(instance.Directory, instance.Id + ".jar");
            await DownloadAsync(clientUrl, target, "游戏客户端 jar", cancellationToken);
            clientJar = target;
        }

        if (clientJar == null || !File.Exists(clientJar))
        {
            throw new LaunchException("缺少游戏客户端 jar，无法构建 ClassPath。");
        }

        Emit($"[启动器] 客户端：{clientJar}", launcher: true);

        var assetsRoot = Path.Combine(instance.RootDirectory, "assets");
        var librariesRoot = Path.Combine(instance.RootDirectory, "libraries");
        var assetIndexId = string.IsNullOrWhiteSpace(metadata.AssetIndexId)
            ? (string.IsNullOrWhiteSpace(metadata.Assets) ? "legacy" : metadata.Assets!)
            : metadata.AssetIndexId!;
        var assetIndexPath = Path.Combine(assetsRoot, "indexes", assetIndexId + ".json");

        if (!File.Exists(assetIndexPath) && metadata.AssetIndexUrl is { Length: > 0 } indexUrl)
        {
            await DownloadAsync(indexUrl, assetIndexPath, $"资源索引 {assetIndexId}.json", cancellationToken);
        }

        if (!File.Exists(assetIndexPath))
        {
            Emit($"[警告] 找不到资源索引 {assetIndexId}.json，游戏可能缺少声音或语言资源。", launcher: true, error: true);
        }

        string? logConfigPath = null;
        if (metadata.Logging is { FileId: { Length: > 0 } logId } logging)
        {
            logConfigPath = Path.Combine(assetsRoot, "log_configs", logId);
            if (!File.Exists(logConfigPath) && logging.FileUrl is { Length: > 0 } logUrl)
            {
                await DownloadAsync(logUrl, logConfigPath, logId, cancellationToken);
            }

            if (!File.Exists(logConfigPath))
            {
                Emit($"[警告] 找不到日志配置 {logId}，游戏控制台输出可能较少。", launcher: true, error: true);
                logConfigPath = null;
            }
        }

        SetStage(LaunchStage.Building, "构建启动参数…");
        var classpath = new List<string>();
        var classpathSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var nativeJars = new List<Library>();
        var missingLibraries = new List<string>();

        void AddClasspath(string path)
        {
            var full = Path.GetFullPath(path);
            if (classpathSet.Add(full))
            {
                classpath.Add(full);
            }
        }

        foreach (var library in metadata.Libraries)
        {
            if (!IsAllowed(library.Rules, features))
            {
                continue;
            }

            if (library.IsNative)
            {
                var nativePath = ResolveNativeJar(library, librariesRoot);
                if (nativePath != null && File.Exists(nativePath))
                {
                    nativeJars.Add(library);
                    continue;
                }

                var download = GetNativeDownload(library);
                if (download?.Url is { Length: > 0 } nativeUrl && nativePath != null)
                {
                    try
                    {
                        await DownloadAsync(nativeUrl, nativePath, $"原生库 {Path.GetFileName(nativePath)}", cancellationToken);
                        nativeJars.Add(library);
                    }
                    catch (Exception ex)
                    {
                        Emit($"[警告] 原生库下载失败 {library.Name}：{ex.Message}", launcher: true, error: true);
                    }
                }
                else
                {
                    Emit($"[警告] 缺少原生库：{library.Name}", launcher: true, error: true);
                }

                continue;
            }

            var relativePath = library.RelativeArtifactPath;
            if (relativePath == null)
            {
                continue;
            }

            var fullPath = Path.Combine(librariesRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (File.Exists(fullPath))
            {
                AddClasspath(fullPath);
                continue;
            }

            if (library.Artifact?.Url is { Length: > 0 } url)
            {
                try
                {
                    await DownloadAsync(url, fullPath, $"库文件 {library.Name}", cancellationToken);
                    AddClasspath(fullPath);
                    continue;
                }
                catch (Exception ex)
                {
                    Emit($"[警告] 库文件下载失败 {library.Name}：{ex.Message}", launcher: true, error: true);
                }
            }

            missingLibraries.Add(library.Name);
        }

        AddClasspath(clientJar);

        if (missingLibraries.Count > 0)
        {
            Emit($"[警告] 仍缺少 {missingLibraries.Count} 个库文件，启动可能失败：", launcher: true, error: true);
            foreach (var name in missingLibraries.Take(8))
            {
                Emit("  · " + name, launcher: true, error: true);
            }
        }

        var nativesDirectory = Path.Combine(AppPaths.CacheDirectory, "natives",
            AppPaths.SanitizeFileName(instance.Id + "-" + instance.MinecraftVersion));
        ExtractNatives(nativeJars, librariesRoot, nativesDirectory);

        var classPathText = string.Join(Path.PathSeparator, classpath);
        var playerName = string.IsNullOrWhiteSpace(request.PlayerName) ? Environment.UserName : request.PlayerName.Trim();
        var online = !string.IsNullOrWhiteSpace(request.AccessToken);
        var uuid = online && !string.IsNullOrWhiteSpace(request.UserUuid)
            ? request.UserUuid
            : OfflineUuid(playerName);
        var accessToken = online ? request.AccessToken! : "00000000000000000000000000000000";
        var userType = online ? (string.IsNullOrWhiteSpace(request.UserType) ? "msa" : request.UserType) : "legacy";

        var replacements = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = playerName,
            ["auth_uuid"] = uuid,
            ["auth_access_token"] = accessToken,
            ["auth_session"] = $"token:{accessToken}:{uuid}",
            ["clientid"] = "",
            ["auth_xuid"] = "",
            ["user_type"] = userType,
            ["user_properties"] = "{}",
            ["version_name"] = instance.Id,
            ["version_type"] = "ILauncher",
            ["game_directory"] = gameDirectory,
            ["assets_root"] = assetsRoot,
            ["assets_index_name"] = assetIndexId,
            ["natives_directory"] = nativesDirectory,
            ["library_directory"] = librariesRoot,
            ["classpath"] = classPathText,
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["launcher_name"] = "ILauncher",
            ["launcher_version"] = "1.1.0",
            ["resolution_width"] = (request.Width ?? 1280).ToString(),
            ["resolution_height"] = (request.Height ?? 720).ToString()
        };

        var arguments = new List<string>();
        var memory = Math.Clamp(request.MemoryMb, 512, 65536);
        arguments.Add($"-Xmx{memory}M");
        arguments.Add($"-Xms{Math.Min(1024, memory / 2)}M");
        arguments.Add("-Dfile.encoding=UTF-8");
        arguments.Add("-Dstdout.encoding=UTF-8");
        arguments.Add("-Dstderr.encoding=UTF-8");
        arguments.Add("-Dminecraft.launcher.brand=ILauncher");
        arguments.Add("-Dminecraft.launcher.version=1.1.0");

        // 用户自定义 JVM 参数放在基础参数之后、版本自带参数之前，
        // 这样像 -XX:+UseG1GC 这类调优参数会生效，而不会被版本默认值覆盖掉顺序。
        if (!string.IsNullOrWhiteSpace(request.ExtraJvmArgs))
        {
            arguments.AddRange(SplitArguments(request.ExtraJvmArgs));
        }

        foreach (var entry in metadata.JvmArguments)
        {
            if (!entry.IsAllowed(features))
            {
                continue;
            }

            foreach (var value in entry.Values)
            {
                arguments.Add(Expand(value, replacements));
            }
        }

        if (logConfigPath != null)
        {
            var template = metadata.Logging?.Argument ?? "-Dlog4j.configurationFile=${path}";
            arguments.Add(template.Replace("${path}", logConfigPath, StringComparison.OrdinalIgnoreCase));
        }

        if (string.IsNullOrWhiteSpace(metadata.MainClass))
        {
            throw new LaunchException("版本文件中没有 mainClass，无法启动。");
        }

        // The main class must come before any game arguments.
        arguments.Add(metadata.MainClass);

        if (metadata.GameArguments.Count > 0)
        {
            foreach (var entry in metadata.GameArguments)
            {
                if (!entry.IsAllowed(features))
                {
                    continue;
                }

                foreach (var value in entry.Values)
                {
                    arguments.Add(Expand(value, replacements));
                }
            }
        }
        else if (!string.IsNullOrWhiteSpace(metadata.LegacyArguments))
        {
            foreach (var value in metadata.LegacyArguments.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                arguments.Add(Expand(value, replacements));
            }
        }

        if (!string.IsNullOrWhiteSpace(request.ServerAddress))
        {
            var address = request.ServerAddress.Trim();
            var colon = address.LastIndexOf(':');
            var host = colon > 0 ? address[..colon] : address;
            var port = colon > 0 && int.TryParse(address[(colon + 1)..], out var parsedPort) ? parsedPort : 25565;
            arguments.Add("--server");
            arguments.Add(host);
            arguments.Add("--port");
            arguments.Add(port.ToString());
        }

        var displayCommand = BuildDisplayCommand(java.Path, arguments);
        Emit($"[启动器] 游戏目录：{gameDirectory}", launcher: true);
        Emit($"[启动器] 资源索引：{assetIndexId}    原生库：{nativesDirectory}", launcher: true);
        Emit($"[启动器] ClassPath 条目：{classpath.Count}    内存：{memory} MB", launcher: true);
        Emit($"[启动器] 启动命令：{displayCommand}", launcher: true);

        return new LaunchPlan
        {
            VersionId = instance.Id,
            JavaPath = java.Path,
            JavaVersion = java.Version,
            JavaMajor = java.Major,
            Arguments = arguments,
            WorkingDirectory = gameDirectory,
            GameDirectory = gameDirectory,
            NativesDirectory = nativesDirectory,
            AssetsIndexId = assetIndexId,
            ClassPath = classPathText,
            LogFilePath = _logFilePath!,
            DisplayCommand = displayCommand,
            MissingLibraries = missingLibraries.Count,
            Warnings = new List<string>()
        };
    }

    private async Task<LaunchResult> StartProcessAsync(LaunchPlan plan, LaunchRequest request, Stopwatch stopwatch, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(plan.JavaPath)
        {
            WorkingDirectory = plan.WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false)
        };

        foreach (var argument in plan.Arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        try
        {
            if (!process.Start())
            {
                throw new LaunchException("无法创建 Java 进程。");
            }
        }
        catch (Exception ex)
        {
            throw new LaunchException($"无法启动 Java 进程：{ex.Message}");
        }

        _process = process;
        Emit($"[启动器] 游戏进程已启动，PID {process.Id}。", launcher: true);
        Emit("────────────── 以下为游戏进程输出 ──────────────", launcher: true);

        SetStage(LaunchStage.WaitingWindow, "等待游戏窗口…");

        var normalizer = new Log4jStreamNormalizer();
        var errorNormalizer = new Log4jStreamNormalizer();
        var stdoutTask = PumpAsync(process.StandardOutput, isError: false, normalizer);
        var stderrTask = PumpAsync(process.StandardError, isError: true, errorNormalizer);
        var windowTask = WatchForWindowAsync(process, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            Stop();
            await process.WaitForExitAsync(CancellationToken.None);
        }

        await Task.WhenAll(stdoutTask, stderrTask);
        foreach (var line in normalizer.FlushPending().Concat(errorNormalizer.FlushPending()))
        {
            Emit(line, launcher: false);
        }

        try
        {
            await windowTask;
        }
        catch (OperationCanceledException)
        {
            // ignored
        }

        var exitCode = process.HasExited ? process.ExitCode : -1;
        stopwatch.Stop();
        Emit("────────────── 游戏进程输出结束 ──────────────", launcher: true);
        Emit($"[启动器] 游戏进程已退出，退出码 {exitCode}，运行时长 {FormatDuration(stopwatch.Elapsed)}。", launcher: true);

        if (_stopRequested)
        {
            SetStage(LaunchStage.Exited, "游戏已停止");
        }
        else if (exitCode == 0)
        {
            SetStage(LaunchStage.Exited, "游戏已退出");
        }
        else
        {
            SetStage(LaunchStage.Failed, $"游戏异常退出（退出码 {exitCode}）");
            Emit("[启动器] 提示：退出码非 0 通常表示游戏崩溃，请查看上方 ERROR / Exception 日志。", launcher: true, error: true);
        }

        return new LaunchResult
        {
            Started = true,
            Success = exitCode == 0 || _stopRequested,
            Stopped = _stopRequested,
            ExitCode = exitCode,
            Duration = stopwatch.Elapsed,
            Plan = plan,
            ProcessId = process.Id
        };
    }

    private async Task PumpAsync(StreamReader reader, bool isError, Log4jStreamNormalizer normalizer)
    {
        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync();
                if (line == null)
                {
                    break;
                }

                if (line.Length > 16000)
                {
                    line = line[..16000] + " …";
                }

                foreach (var normalized in normalizer.Process(line))
                {
                    if (!_windowDetected && LooksLikeGameReady(normalized))
                    {
                        _windowDetected = true;
                        SetStage(LaunchStage.Running, "游戏运行中");
                        Emit("[启动器] 已检测到游戏完成初始化，游戏运行中。", launcher: true);
                    }

                    Emit(normalized, launcher: false, error: isError);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // Stream closed when the process exits.
        }
    }

    private async Task WatchForWindowAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested && !process.HasExited)
        {
            try
            {
                process.Refresh();
                if (process.MainWindowHandle != IntPtr.Zero && !_windowDetected)
                {
                    _windowDetected = true;
                    SetStage(LaunchStage.Running, "游戏运行中");
                    Emit("[启动器] 已检测到游戏窗口，游戏运行中。", launcher: true);
                    return;
                }
            }
            catch
            {
                // ignored
            }

            try
            {
                await Task.Delay(400, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private void ExtractNatives(List<Library> nativeJars, string librariesRoot, string nativesDirectory)
    {
        if (nativeJars.Count == 0)
        {
            Directory.CreateDirectory(nativesDirectory);
            return;
        }

        var signature = string.Join("|", nativeJars.Select(library =>
        {
            var path = ResolveNativeJar(library, librariesRoot) ?? "";
            var info = File.Exists(path) ? new FileInfo(path) : null;
            return $"{path}:{info?.Length}:{info?.LastWriteTimeUtc.Ticks}";
        }));
        var signatureHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(signature)));
        var marker = Path.Combine(nativesDirectory, ".ilauncher-natives");
        if (File.Exists(marker) && File.ReadAllText(marker).Trim() == signatureHash)
        {
            return;
        }

        if (Directory.Exists(nativesDirectory))
        {
            try
            {
                Directory.Delete(nativesDirectory, recursive: true);
            }
            catch
            {
                // ignored
            }
        }

        Directory.CreateDirectory(nativesDirectory);
        var count = 0;
        foreach (var library in nativeJars)
        {
            var path = ResolveNativeJar(library, librariesRoot);
            if (path == null || !File.Exists(path))
            {
                continue;
            }

            try
            {
                using var archive = ZipFile.OpenRead(path);
                foreach (var entry in archive.Entries)
                {
                    var name = entry.FullName.Replace('\\', '/');
                    if (string.IsNullOrEmpty(entry.Name) ||
                        name.StartsWith("META-INF/", StringComparison.OrdinalIgnoreCase) ||
                        name.StartsWith("META-INF", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".class", StringComparison.OrdinalIgnoreCase) ||
                        library.ExtractExclude.Any(ex => name.StartsWith(ex, StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var target = Path.Combine(nativesDirectory, name.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    entry.ExtractToFile(target, overwrite: true);
                    count++;
                }
            }
            catch (Exception ex)
            {
                Emit($"[警告] 解压原生库失败 {Path.GetFileName(path)}：{ex.Message}", launcher: true, error: true);
            }
        }

        try
        {
            File.WriteAllText(marker, signatureHash);
        }
        catch
        {
            // ignored
        }

        Emit($"[启动器] 已解压 {count} 个原生库文件。", launcher: true);
    }

    private static string? ResolveNativeJar(Library library, string librariesRoot)
    {
        if (library.Artifact?.Path is { Length: > 0 } artifactPath &&
            library.Name.Split(':').Length >= 4 &&
            library.Name.Split(':')[3].StartsWith("natives-", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine(librariesRoot, artifactPath.Replace('/', Path.DirectorySeparatorChar));
        }

        if (library.Natives.TryGetValue(RuleContext.OsName, out var classifier))
        {
            var arch = RuleContext.OsArch switch
            {
                "x86_64" => "64",
                "x86" => "32",
                _ => ""
            };
            classifier = classifier.Replace("${arch}", arch, StringComparison.OrdinalIgnoreCase);
            var relative = Library.MavenPath($"{library.Name}:{classifier}");
            if (relative != null)
            {
                return Path.Combine(librariesRoot, relative.Replace('/', Path.DirectorySeparatorChar));
            }
        }

        return null;
    }

    private static DownloadInfo? GetNativeDownload(Library library)
    {
        if (library.Artifact?.Url is { Length: > 0 })
        {
            return library.Artifact;
        }

        if (library.Natives.TryGetValue(RuleContext.OsName, out var classifier))
        {
            if (library.Classifiers.TryGetValue(classifier, out var download))
            {
                return download;
            }
        }

        return null;
    }

    private static bool IsAllowed(List<Rule> rules, IReadOnlyDictionary<string, bool> features)
    {
        if (rules.Count == 0)
        {
            return true;
        }

        var allowed = false;
        foreach (var rule in rules)
        {
            if (rule.Matches(features))
            {
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
            }
        }

        return allowed;
    }

    private static bool LooksLikeGameReady(string line)
        => line.Contains("Sound engine started", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Created: ", StringComparison.OrdinalIgnoreCase) && line.Contains("minecraft:textures", StringComparison.OrdinalIgnoreCase)
           || line.Contains("Backend library: LWJGL", StringComparison.OrdinalIgnoreCase)
           || line.Contains("OpenGL debug message", StringComparison.OrdinalIgnoreCase);

    private string Expand(string value, IReadOnlyDictionary<string, string> replacements)
    {
        if (!value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        var expanded = PlaceholderRegex().Replace(value, match =>
        {
            var key = match.Groups[1].Value;
            if (replacements.TryGetValue(key, out var replacement))
            {
                return replacement;
            }

            if (_unknownPlaceholders++ < 3)
            {
                Emit($"[警告] 未知参数占位符：${{{key}}}", launcher: true, error: true);
            }

            return "";
        });

        return expanded;
    }

    private static string BuildDisplayCommand(string javaPath, IEnumerable<string> arguments)
    {
        var builder = new StringBuilder();
        builder.Append('"').Append(javaPath).Append('"');
        var maskNext = false;
        foreach (var argument in arguments)
        {
            builder.Append(' ');
            var value = maskNext ? "***" : argument;
            maskNext = IsSecretArgument(argument);
            if (value.Contains(' ') || value.Contains('"'))
            {
                builder.Append('"').Append(value.Replace("\"", "\\\"")).Append('"');
            }
            else
            {
                builder.Append(value);
            }
        }

        return builder.ToString();
    }

    /// <summary>Arguments whose following value must never reach the on-disk log.</summary>
    private static bool IsSecretArgument(string name) =>
        name.Equals("--accessToken", StringComparison.OrdinalIgnoreCase)
        || name.Equals("--authSession", StringComparison.OrdinalIgnoreCase)
        || name.Equals("--session", StringComparison.OrdinalIgnoreCase)
        || name.Equals("--clientSecret", StringComparison.OrdinalIgnoreCase);

    private static string OfflineUuid(string playerName)
    {
        var bytes = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x30);
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    private static string FormatDuration(TimeSpan duration)
        => duration.TotalHours >= 1
            ? $"{(int)duration.TotalHours} 小时 {duration.Minutes} 分 {duration.Seconds} 秒"
            : duration.TotalMinutes >= 1
                ? $"{duration.Minutes} 分 {duration.Seconds} 秒"
                : $"{duration.TotalSeconds:0.0} 秒";

    private void SetStage(LaunchStage stage, string message)
    {
        Stage = stage;
        StageChanged?.Invoke(this, new LaunchStageEventArgs { Stage = stage, Message = message });
    }

    private void Emit(string line, bool launcher = false, bool error = false)
    {
        LogReceived?.Invoke(this, new LaunchLogEventArgs
        {
            Line = line,
            IsLauncherLine = launcher,
            IsError = error,
            Timestamp = DateTime.Now
        });
        WriteLogRaw(launcher ? $"[ILauncher] {line}" : line);
    }

    private void OpenLogFile(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            _logWriter = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        }
        catch
        {
            _logWriter = null;
        }
    }

    private void WriteLogRaw(string line)
    {
        lock (_logLock)
        {
            try
            {
                _logWriter?.WriteLine(line);
            }
            catch
            {
                // ignored
            }
        }
    }

    private void CloseLogFile()
    {
        lock (_logLock)
        {
            try
            {
                _logWriter?.Flush();
                _logWriter?.Dispose();
            }
            catch
            {
                // ignored
            }

            _logWriter = null;
        }
    }

    private async Task DownloadAsync(string url, string destination, string label, CancellationToken cancellationToken)
    {
        if (File.Exists(destination))
        {
            return;
        }

        Emit($"[启动器] 下载 {label} …", launcher: true);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".tmp";
        try
        {
            using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            await using (var file = File.Create(temp))
            {
                await response.Content.CopyToAsync(file, cancellationToken);
            }

            File.Move(temp, destination, overwrite: true);
            Emit($"[启动器] 已下载 {label}（{new FileInfo(destination).Length / 1024d / 1024d:0.0} MB）", launcher: true);
        }
        catch
        {
            try
            {
                if (File.Exists(temp))
                {
                    File.Delete(temp);
                }
            }
            catch
            {
                // ignored
            }

            throw;
        }
    }

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ILauncher/1.1.0");
        return client;
    }

    [GeneratedRegex(@"\$\{([^}]+)\}")]
    private static partial Regex PlaceholderRegex();
    /// <summary>把用户输入的参数字符串拆开，支持双引号包裹含空格的参数。</summary>
    private static IEnumerable<string> SplitArguments(string text)
    {
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var ch in text)
        {
            if (ch == '"')
            {
                quoted = !quoted;
                continue;
            }

            if (!quoted && char.IsWhiteSpace(ch))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }

                continue;
            }

            current.Append(ch);
        }

        if (current.Length > 0)
        {
            yield return current.ToString();
        }
    }
}