namespace ILauncher.Core.Models;

public sealed record LaunchRequest
{
    public required GameInstance Instance { get; init; }

    public string PlayerName { get; init; } = "Player";

    public int MemoryMb { get; init; } = 4096;

    public string? JavaPath { get; init; }

    /// <summary>额外的 JVM 参数（来自启动器或版本设置），原样拼在自动生成的参数之后。</summary>
    public string? ExtraJvmArgs { get; init; }

    public string? GameDirectory { get; init; }

    public string? ServerAddress { get; init; }

    public string? AccessToken { get; init; }

    public string? UserUuid { get; init; }

    public string? UserType { get; init; }

    public int? Width { get; init; }

    public int? Height { get; init; }

    /// <summary>Test launches keep the launcher open and surface every log line.</summary>
    public bool TestMode { get; init; }

    public string? LogDirectory { get; init; }
}

public sealed record LaunchPlan
{
    public required string VersionId { get; init; }
    public required string JavaPath { get; init; }
    public required string JavaVersion { get; init; }
    public required int JavaMajor { get; init; }
    public required IReadOnlyList<string> Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public required string GameDirectory { get; init; }
    public required string NativesDirectory { get; init; }
    public required string AssetsIndexId { get; init; }
    public required string ClassPath { get; init; }
    public required string LogFilePath { get; init; }
    public required string DisplayCommand { get; init; }
    public required int MissingLibraries { get; init; }
    public required IReadOnlyList<string> Warnings { get; init; }
}

public enum LaunchStage
{
    Idle,
    Checking,
    Building,
    Starting,
    WaitingWindow,
    Running,
    Stopping,
    Exited,
    Failed
}

public sealed class LaunchStageEventArgs : EventArgs
{
    public required LaunchStage Stage { get; init; }
    public string Message { get; init; } = "";
}

public sealed class LaunchLogEventArgs : EventArgs
{
    public required string Line { get; init; }
    public bool IsError { get; init; }
    public bool IsLauncherLine { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.Now;
}

public sealed class LaunchResult
{
    public required bool Started { get; init; }
    public required bool Success { get; init; }
    public int ExitCode { get; init; } = -1;
    public bool Stopped { get; init; }
    public TimeSpan Duration { get; init; }
    public string? FailureReason { get; init; }
    public LaunchPlan? Plan { get; init; }
    public int? ProcessId { get; init; }
}
