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
        Action<long, long>? progress, CancellationToken cancellationToken)
    {
        if (File.Exists(destination) && new FileInfo(destination).Length > 0)
        {
            progress?.Invoke(1, 1);
            return;
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

            File.Move(temp, destination, overwrite: true);
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
