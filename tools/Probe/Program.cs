using System.Text;
using ILauncher.Core.Models;
using ILauncher.Core.Services;

Console.OutputEncoding = Encoding.UTF8;

var settings = new SettingsService();
var installations = new InstallationService(settings);
var root = settings.Settings.MinecraftRoot;
if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
{
    root = installations.ResolveRoot() ?? "";
}

if (string.IsNullOrWhiteSpace(root))
{
    Console.Error.WriteLine("No Minecraft installation found.");
    return 1;
}

Console.WriteLine($"Root: {root}");
var instances = await installations.LoadInstancesAsync(root);
Console.WriteLine($"Instances: {instances.Count}");
foreach (var item in instances)
{
    Console.WriteLine($"  {item.Id,-34} {item.Subtitle,-28} mods={item.ModCount,3}/{item.EnabledModCount,-3} java={item.RequiredJavaMajor}");
}

var versionId = args.Length > 0 ? args[0] : "1.20.1-Forge_47.4.20";
var instance = instances.FirstOrDefault(i => string.Equals(i.Id, versionId, StringComparison.OrdinalIgnoreCase));
if (instance == null)
{
    Console.Error.WriteLine($"Version not found: {versionId}");
    return 2;
}

var runSeconds = args.Length > 1 && int.TryParse(args[1], out var seconds) ? seconds : 20;

var java = new JavaService(settings);
var launch = new LaunchService(java);
launch.LogReceived += (_, e) => Console.WriteLine($"[{e.Timestamp:HH:mm:ss.fff}] {(e.IsError ? "ERR " : "")}{e.Line}");
launch.StageChanged += (_, e) => Console.WriteLine($"=== STAGE {e.Stage}: {e.Message} ===");

using var cts = new CancellationTokenSource();
var runTask = launch.RunAsync(new LaunchRequest
{
    Instance = instance,
    PlayerName = settings.ResolvePlayerName(),
    MemoryMb = 4096,
    TestMode = true
}, cts.Token);

var stopTimer = Task.Run(async () =>
{
    try
    {
        await Task.Delay(TimeSpan.FromSeconds(runSeconds));
        if (launch.IsRunning)
        {
            Console.WriteLine($"=== Probe stopping game after {runSeconds}s ===");
            launch.Stop();
        }
    }
    catch (TaskCanceledException)
    {
        // ignored
    }
});

var result = await runTask;
await stopTimer;
Console.WriteLine($"=== RESULT started={result.Started} success={result.Success} exit={result.ExitCode} duration={result.Duration.TotalSeconds:0.0}s reason={result.FailureReason} ===");
return result.Started && result.Success ? 0 : 3;
