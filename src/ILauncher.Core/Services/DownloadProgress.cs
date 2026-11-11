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
    private const int MaxAttempts = 3;
    private static readonly HttpClient Client = CreateClient();

    public static HttpClient HttpClient => Client;

    public static Task<string> GetStringAsync(string url, CancellationToken cancellationToken) =>
        ExecuteWithRetryAsync(url, cancellationToken, async client =>
        {
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync(cancellationToken);
        });

    public static Task<byte[]> GetBytesAsync(string url, CancellationToken cancellationToken) =>
        ExecuteWithRetryAsync(url, cancellationToken, async client =>
        {
            using var response = await client.GetAsync(url, cancellationToken);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        });

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
            await ExecuteWithRetryAsync(url, cancellationToken, async client =>
            {
                TryDelete(temp);
                using var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
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
                return true;
            });
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    /// <summary>
    /// Runs a request, retrying transient network failures. The last attempt switches to a
    /// one-shot HTTP/1.1 client because half-dead HTTP/2 connections are a common cause of
    /// "Error while copying content to a stream" on unstable networks.
    /// </summary>
    private static async Task<T> ExecuteWithRetryAsync<T>(string url, CancellationToken cancellationToken,
        Func<HttpClient, Task<T>> action)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var client = attempt < MaxAttempts ? Client : CreateClient(http11: true);
            try
            {
                return await action(client);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (HttpRequestException ex) when (ex.StatusCode is not null)
            {
                throw new HttpRequestException(
                    $"服务器返回错误：HTTP {(int)ex.StatusCode}（{DescribeHost(url)}）。", ex);
            }
            catch (Exception ex) when (!IsTransient(ex))
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                if (attempt < MaxAttempts)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt * attempt), cancellationToken);
                }
            }
            finally
            {
                if (!ReferenceEquals(client, Client))
                {
                    client.Dispose();
                }
            }
        }

        throw new HttpRequestException(
            $"网络请求失败（已重试 {MaxAttempts} 次）：{DescribeHost(url)}。请检查网络连接，或在设置中切换下载源。",
            last);
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        HttpRequestException => true,
        IOException => true,
        TaskCanceledException => true,
        _ => ex.InnerException is not null && IsTransient(ex.InnerException)
    };

    private static string DescribeHost(string url)
    {
        try
        {
            return new Uri(url).Host;
        }
        catch
        {
            return url;
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

    private static HttpClient CreateClient(bool http11 = false)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = TimeSpan.FromSeconds(20),
            AutomaticDecompression = System.Net.DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5)
        };
        var client = new HttpClient(handler) { Timeout = TimeSpan.FromMinutes(5) };
        if (http11)
        {
            client.DefaultRequestVersion = System.Net.HttpVersion.Version11;
            client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        }

        client.DefaultRequestHeaders.UserAgent.ParseAdd("ILauncher/1.1 (Minecraft launcher)");
        return client;
    }
}
