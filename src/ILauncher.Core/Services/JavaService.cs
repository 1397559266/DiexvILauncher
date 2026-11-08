using System.Diagnostics;
using System.Text.RegularExpressions;

namespace ILauncher.Core.Services;

public sealed record JavaInfo(string Path, string Version, int Major)
{
    public string Display => $"Java {Major}  ·  {Version}";
}

public sealed partial class JavaService
{
    private readonly SettingsService _settings;
    private readonly List<JavaInfo> _cache = new();
    private readonly SemaphoreSlim _discoveryLock = new(1, 1);

    public JavaService(SettingsService settings)
    {
        _settings = settings;
    }

    public IReadOnlyList<JavaInfo> Cached => _cache;

    public async Task<IReadOnlyList<JavaInfo>> DiscoverAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        if (!force && _cache.Count > 0)
        {
            return _cache;
        }

        await _discoveryLock.WaitAsync(cancellationToken);
        try
        {
            if (!force && _cache.Count > 0)
            {
                return _cache;
            }

            var results = new List<JavaInfo>();
            foreach (var path in CollectCandidates())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var info = await ProbeAsync(path, cancellationToken);
                if (info != null && results.All(r => !string.Equals(r.Path, info.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    results.Add(info);
                }
            }

            results.Sort((a, b) => b.Major.CompareTo(a.Major));
            _cache.Clear();
            _cache.AddRange(results);
            return _cache;
        }
        finally
        {
            _discoveryLock.Release();
        }
    }

    public async Task<JavaInfo?> ResolveAsync(int requiredMajor, string? preferredPath, CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(preferredPath))
        {
            var preferred = await ProbeAsync(preferredPath, cancellationToken);
            if (preferred != null)
            {
                return preferred;
            }
        }

        var installed = await DiscoverAsync(cancellationToken: cancellationToken);
        var exact = installed.FirstOrDefault(j => j.Major == requiredMajor);
        if (exact != null)
        {
            return exact;
        }

        var newer = installed.Where(j => j.Major > requiredMajor).OrderBy(j => j.Major).FirstOrDefault();
        return newer ?? installed.OrderByDescending(j => j.Major).FirstOrDefault();
    }

    public async Task<JavaInfo?> ProbeAsync(string javaPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(javaPath))
        {
            return null;
        }

        try
        {
            var startInfo = new ProcessStartInfo(javaPath)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8
            };
            startInfo.ArgumentList.Add("-version");

            using var process = Process.Start(startInfo);
            if (process == null)
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // ignored
                }

                return null;
            }

            var output = (await stdoutTask) + "\n" + (await stderrTask);
            var match = JavaVersionRegex().Match(output);
            if (!match.Success)
            {
                return null;
            }

            var rawVersion = match.Groups[1].Value;
            var parts = rawVersion.Split('.', '_', '-', '+');
            if (parts.Length == 0 || !int.TryParse(parts[0], out var major))
            {
                return null;
            }

            // Legacy versions report 1.8.0_451, modern ones report 17.0.15 or 21.
            if (major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacyMajor))
            {
                major = legacyMajor;
            }

            return new JavaInfo(javaPath, rawVersion, major);
        }
        catch
        {
            return null;
        }
    }

    private List<string> CollectCandidates()
    {
        var paths = new List<string>();

        void Add(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(path.Trim().Trim('"'));
                if (File.Exists(full))
                {
                    paths.Add(full);
                }
            }
            catch
            {
                // ignored
            }
        }

        Add(_settings.Settings.JavaPath);
        Add(Path.Combine(Environment.GetEnvironmentVariable("JAVA_HOME") ?? "", "bin", "java.exe"));

        var bundledRoot = Path.Combine(AppPaths.DataDirectory, "java");
        if (Directory.Exists(bundledRoot))
        {
            try
            {
                foreach (var directory in Directory.EnumerateDirectories(bundledRoot))
                {
                    Add(Path.Combine(directory, "bin", "java.exe"));
                    foreach (var nested in Directory.EnumerateDirectories(directory))
                    {
                        Add(Path.Combine(nested, "bin", "java.exe"));
                    }
                }
            }
            catch
            {
                // ignored
            }
        }

        foreach (var root in new[]
                 {
                     @"C:\Program Files\Java",
                     @"C:\Program Files\Eclipse Adoptium",
                     @"C:\Program Files\Microsoft",
                     @"C:\Program Files\Zulu",
                     @"C:\Program Files\BellSoft",
                     @"C:\Program Files\Amazon Corretto",
                     @"C:\MCmods",
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".jdks")
                 })
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            try
            {
                foreach (var directory in Directory.EnumerateDirectories(root))
                {
                    var name = Path.GetFileName(directory);
                    if (!name.Contains("jdk", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("java", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("jre", StringComparison.OrdinalIgnoreCase) &&
                        !name.Contains("temurin", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    Add(Path.Combine(directory, "bin", "java.exe"));
                }
            }
            catch
            {
                // ignored
            }
        }

        var pathVariable = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var part in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(part.Trim(), "java.exe");
                if (File.Exists(candidate))
                {
                    paths.Add(Path.GetFullPath(candidate));
                }
            }
            catch
            {
                // ignored
            }
        }

        return paths.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    [GeneratedRegex(@"version\s+""([^""]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex JavaVersionRegex();
}
