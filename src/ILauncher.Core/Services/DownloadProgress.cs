namespace ILauncher.Core.Services;

public sealed record DownloadProgress
{
    public string Phase { get; init; } = "";
    public string Message { get; init; } = "";
    public double Percent { get; init; } = -1;
    public long Completed { get; init; }
    public long Total { get; init; }
}

public static class DownloadHelper
{
    private static readonly HttpClient Client = CreateClient();

    public static HttpClient HttpClient => Client;

    public static async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    public static async Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken)
    {
        using var response = await Client.GetAsync(url, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    public static async Task DownloadFileAsync(string url, string destination,
        Action<long, long>? progress, CancellationToken cancellationToken,
        string? expectedSha1 = null, string? expectedSha256 = null)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length > 0)
        {
            if (await MatchesAsync(destination, expectedSha1, expectedSha256, cancellationToken))
            {
                progress?.Invoke(1, 1);
                return;
            }

            TryDelete(destination);
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".tmp";
        try
        {
            // Use a fresh connection pool for file transfers. Some CDNs keep a
            // half-dead HTTP/2 connection alive after an idle period; a one-shot
            // client makes retries and large downloads much more reliable.
            using var client = CreateClient();
            using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();
            var total = response.Content.Headers.ContentLength ?? 0;
            await using (var source = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var target = File.Create(temp))
            {
                var buffer = new byte[81920];
                long readTotal = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                    readTotal += read;
                    progress?.Invoke(readTotal, total);
                }
            }

            if (!await MatchesAsync(temp, expectedSha1, expectedSha256, cancellationToken))
            {
                throw new InvalidOperationException(
                    $"下载文件校验失败：{Path.GetFileName(destination)}（镜像或网络返回的内容与官方哈希不一致）。");
            }

            File.Move(temp, destination, overwrite: true);
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>True when the file matches the expected digest, or when no digest was supplied.</summary>
    private static async Task<bool> MatchesAsync(string path, string? sha1, string? sha256,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(sha1) && string.IsNullOrWhiteSpace(sha256))
        {
            return true;
        }

        try
        {
            await using var stream = File.OpenRead(path);
            if (!string.IsNullOrWhiteSpace(sha1))
            {
                var actual = await System.Security.Cryptography.SHA1.HashDataAsync(stream, cancellationToken);
                return string.Equals(Convert.ToHexString(actual), sha1.Trim(), StringComparison.OrdinalIgnoreCase);
            }

            var actual256 = await System.Security.Cryptography.SHA256.HashDataAsync(stream, cancellationToken);
            return string.Equals(Convert.ToHexString(actual256), sha256!.Trim(), StringComparison.OrdinalIgnoreCase);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // ignored
        }
    }

    private static HttpClient CreateClient()
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ILauncher/1.1 (Minecraft launcher)");
        return client;
    }
}
