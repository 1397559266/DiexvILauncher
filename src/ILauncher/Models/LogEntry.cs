using System.Text.RegularExpressions;
using ILauncher.Localization;

namespace ILauncher.Models;

public enum LogLevelKind
{
    Info,
    Warn,
    Error,
    Debug,
    Trace,
    Launcher
}

public sealed class LogEntry
{
    public required DateTime Timestamp { get; init; }
    public required string Time { get; init; }
    public required string Level { get; init; }
    public required string Message { get; init; }
    public required LogLevelKind Kind { get; init; }
    public required string SearchText { get; init; }
    public bool IsLauncher { get; init; }

    /// <summary>Launcher log lines follow the UI language; game output is untouched.</summary>
    public string DisplayMessage => IsLauncher ? LanguageService.Translate(Message) : Message;

    public bool IsProblem => Kind is LogLevelKind.Error or LogLevelKind.Warn;
}

public static partial class LogEntryParser
{
    private static readonly Regex LevelRegex = new(
        @"\[(?<thread>[^\]/]+)/(?<level>TRACE|DEBUG|INFO|WARN|ERROR|FATAL)\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex AnsiRegex = new(@"\x1B\[[0-9;]*[A-Za-z]", RegexOptions.Compiled);

    public static LogEntry Parse(string line, DateTime timestamp, bool isLauncherLine = false)
    {
        var message = AnsiRegex.Replace(line, "");
        var kind = LogLevelKind.Info;
        var level = "INFO";

        if (isLauncherLine || message.StartsWith("[启动器]", StringComparison.Ordinal))
        {
            kind = LogLevelKind.Launcher;
            level = "IL";
            isLauncherLine = true;
        }
        else
        {
            var match = LevelRegex.Match(message);
            if (match.Success)
            {
                level = match.Groups["level"].Value.ToUpperInvariant();
                kind = ParseKind(level);
            }
            else if (message.Contains("Exception", StringComparison.OrdinalIgnoreCase) ||
                     message.Contains("FATAL", StringComparison.Ordinal) ||
                     message.Contains("ERROR", StringComparison.Ordinal))
            {
                kind = LogLevelKind.Error;
                level = "ERROR";
            }
            else if (message.Contains("WARN", StringComparison.Ordinal))
            {
                kind = LogLevelKind.Warn;
                level = "WARN";
            }
            else if (message.Contains("DEBUG", StringComparison.Ordinal))
            {
                kind = LogLevelKind.Debug;
                level = "DEBUG";
            }
            else if (message.Contains("TRACE", StringComparison.Ordinal))
            {
                kind = LogLevelKind.Trace;
                level = "TRACE";
            }
        }

        return new LogEntry
        {
            Timestamp = timestamp,
            Time = timestamp.ToString("HH:mm:ss.fff"),
            Level = level,
            Message = message,
            Kind = kind,
            SearchText = message.ToLowerInvariant(),
            IsLauncher = isLauncherLine
        };
    }

    private static LogLevelKind ParseKind(string level) => level switch
    {
        "ERROR" or "FATAL" => LogLevelKind.Error,
        "WARN" => LogLevelKind.Warn,
        "DEBUG" => LogLevelKind.Debug,
        "TRACE" => LogLevelKind.Trace,
        _ => LogLevelKind.Info
    };
}
