using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ILauncher.Core.Services;

/// <summary>浏览器回调里带回的授权结果。</summary>
public sealed record OAuthCallback(string? Code, string? State, string? Error, string? ErrorDescription);

/// <summary>
/// 一个只接受一次授权回调的极简回环 HTTP 服务器。
/// 刻意用 <see cref="TcpListener"/> 而不是 <c>HttpListener</c>：
/// HttpListener 走 http.sys，需要 URL ACL（普通用户通常要管理员权限才能注册前缀），
/// 而直接监听 127.0.0.1 / ::1 不需要任何特权，也不会弹出防火墙提示。
/// </summary>
public sealed class LoopbackOAuthListener : IDisposable
{
    private const int MaxRequestBytes = 16 * 1024;
    private static readonly TimeSpan ReadTimeout = TimeSpan.FromSeconds(15);

    private readonly int _requestedPort;
    private readonly string _path;
    private readonly List<TcpListener> _listeners = [];
    private bool _started;
    private bool _disposed;

    public LoopbackOAuthListener(int port, string path = "/callback")
    {
        _requestedPort = port is > 0 and < 65536 ? port : 0;
        var trimmed = string.IsNullOrWhiteSpace(path) ? "/callback" : path.Trim();
        _path = trimmed.StartsWith('/') ? trimmed : "/" + trimmed;
    }

    /// <summary>实际绑定的端口（构造时传 0 则由系统分配）。</summary>
    public int Port { get; private set; }

    /// <summary>需要登记到 Azure 应用注册里的重定向 URI。</summary>
    public string RedirectUri => $"http://localhost:{Port}{_path}";

    public void Start()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(LoopbackOAuthListener));
        }

        if (_started)
        {
            throw new InvalidOperationException("回调监听器已经启动。");
        }

        var port = _requestedPort;
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                var ipv6 = new TcpListener(IPAddress.IPv6Loopback, port);
                ipv6.Start();
                _listeners.Add(ipv6);
                port = ((IPEndPoint)ipv6.LocalEndpoint).Port;
            }
            catch (SocketException)
            {
                // 没有 IPv6 回环时退回 IPv4。
            }
        }

        try
        {
            var ipv4 = new TcpListener(IPAddress.Loopback, port);
            ipv4.Start();
            _listeners.Add(ipv4);
            port = ((IPEndPoint)ipv4.LocalEndpoint).Port;
        }
        catch (SocketException ex)
        {
            // 指定了端口却没能监听 IPv4：浏览器很可能解析到 127.0.0.1，直接失败更诚实。
            if (_requestedPort != 0 || _listeners.Count == 0)
            {
                Dispose();
                throw new InvalidOperationException(_requestedPort == 0
                    ? $"无法在本地回环地址监听回调端口：{ex.Message}"
                    : $"本地端口 {_requestedPort} 已被占用或不可用，请稍后重试。", ex);
            }
        }

        Port = port;
        _started = true;
    }

    /// <summary>等待浏览器回调；超时抛 <see cref="TimeoutException"/>。</summary>
    public async Task<OAuthCallback> WaitForCallbackAsync(TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        if (!_started)
        {
            throw new InvalidOperationException("回调监听器尚未启动。");
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        linked.CancelAfter(timeout);
        try
        {
            while (true)
            {
                using var client = await AcceptAsync(linked.Token).ConfigureAwait(false);
                using var readCts = CancellationTokenSource.CreateLinkedTokenSource(linked.Token);
                readCts.CancelAfter(ReadTimeout);
                var request = await ReadRequestAsync(client, readCts.Token).ConfigureAwait(false);

                if (!string.Equals(request.Path, _path, StringComparison.OrdinalIgnoreCase))
                {
                    // 浏览器会顺手请求 /favicon.ico 之类，忽略并继续等真正的回调。
                    await WriteResponseAsync(client, 404, "Not Found",
                        BuildPage("页面不存在", "这里不是登录回调地址。"), linked.Token).ConfigureAwait(false);
                    continue;
                }

                var query = ParseQuery(request.Query);
                query.TryGetValue("code", out var code);
                query.TryGetValue("state", out var state);
                query.TryGetValue("error", out var error);
                query.TryGetValue("error_description", out var description);

                var succeeded = string.IsNullOrEmpty(error) && !string.IsNullOrEmpty(code);
                await WriteResponseAsync(client, 200, "OK",
                    BuildPage(succeeded ? "登录成功" : "登录未完成",
                        succeeded
                            ? "已经拿到授权，可以关闭这个页面并回到启动器。"
                            : "授权没有完成，请回到启动器重试。"), CancellationToken.None).ConfigureAwait(false);

                return new OAuthCallback(code, state, error, description);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("等待浏览器回调超时。");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var listener in _listeners)
        {
            try
            {
                listener.Stop();
            }
            catch
            {
                // 关停失败没有补救手段，也不该影响登录结果。
            }
        }

        _listeners.Clear();
    }

    private async Task<TcpClient> AcceptAsync(CancellationToken cancellationToken)
    {
        var pending = _listeners.Select(listener => listener.AcceptTcpClientAsync(cancellationToken).AsTask()).ToList();
        var completed = await Task.WhenAny(pending).ConfigureAwait(false);
        foreach (var task in pending)
        {
            if (!ReferenceEquals(task, completed))
            {
                // 避免取消时留下未观察的异常。
                _ = task.ContinueWith(static t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
            }
        }

        return await completed.ConfigureAwait(false);
    }

    private static async Task<(string Method, string Path, string Query)> ReadRequestAsync(TcpClient client,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        var buffer = new byte[MaxRequestBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            length += read;
            if (FindHeaderEnd(buffer, length) >= 0)
            {
                break;
            }
        }

        if (length == 0)
        {
            return ("", "/", "");
        }

        var head = Encoding.ASCII.GetString(buffer, 0, length);
        var lineEnd = head.IndexOf("\r\n", StringComparison.Ordinal);
        var requestLine = lineEnd >= 0 ? head[..lineEnd] : head;
        var parts = requestLine.Split(' ');
        if (parts.Length < 2)
        {
            return ("", "/", "");
        }

        var target = parts[1];
        var question = target.IndexOf('?');
        var path = question < 0 ? target : target[..question];
        var query = question < 0 ? "" : target[(question + 1)..];
        return (parts[0].ToUpperInvariant(), path, query);
    }

    private static int FindHeaderEnd(byte[] buffer, int length)
    {
        for (var i = 3; i < length; i++)
        {
            if (buffer[i] == '\n' && buffer[i - 1] == '\r' && buffer[i - 2] == '\n' && buffer[i - 3] == '\r')
            {
                return i;
            }
        }

        return -1;
    }

    private static async Task WriteResponseAsync(TcpClient client, int status, string reason, string html,
        CancellationToken cancellationToken)
    {
        try
        {
            var body = Encoding.UTF8.GetBytes(html);
            var header = $"HTTP/1.1 {status} {reason}\r\n" +
                         "Content-Type: text/html; charset=utf-8\r\n" +
                         $"Content-Length: {body.Length}\r\n" +
                         "Cache-Control: no-store\r\n" +
                         "Connection: close\r\n\r\n";
            var stream = client.GetStream();
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // 用户可能已经把浏览器关掉了，这不是错误。
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(query))
        {
            return result;
        }

        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=');
            var key = WebUtility.UrlDecode(index < 0 ? pair : pair[..index]);
            var value = index < 0 ? "" : WebUtility.UrlDecode(pair[(index + 1)..]);
            if (!string.IsNullOrEmpty(key) && !result.ContainsKey(key))
            {
                result[key] = value;
            }
        }

        return result;
    }

    private static string BuildPage(string title, string message) => $$"""
        <!doctype html>
        <html lang="zh-CN">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{{title}}</title>
        <style>
        body{font-family:"Segoe UI","Microsoft YaHei",system-ui,sans-serif;background:#eef2f7;color:#1f2933;
        display:flex;align-items:center;justify-content:center;height:100vh;margin:0}
        .card{background:#fff;border-radius:16px;padding:32px 40px;max-width:420px;text-align:center;
        box-shadow:0 12px 32px rgba(15,23,42,.10)}
        h1{font-size:20px;margin:0 0 10px}p{font-size:13px;line-height:1.8;color:#52606d;margin:0}
        </style>
        </head>
        <body><div class="card"><h1>{{title}}</h1><p>{{message}}</p></div></body>
        </html>
        """;
}
