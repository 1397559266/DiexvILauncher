using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ILauncher.Core.Services;

public sealed record MicrosoftDeviceCode(string DeviceCode, string UserCode, string VerificationUri,
    int IntervalSeconds, int ExpiresInSeconds, string Message);

public sealed record MicrosoftAccount(string Name, string Uuid, string AccessToken, string RefreshToken,
    string? SkinUrl, string? LoginHint = null);

/// <summary>Azure 应用是否登记了我们要用的回环重定向 URI。</summary>
public enum MicrosoftRedirectStatus
{
    Registered,
    NotRegistered,
    Unknown
}

public sealed record MicrosoftRedirectCheck(MicrosoftRedirectStatus Status, string? Detail = null);

/// <summary>预检挑出来的回调地址，以及它的登记状态。</summary>
public sealed record MicrosoftRedirectSelection(string RedirectUri, MicrosoftRedirectStatus Status,
    string? Detail = null);

/// <summary>
/// Microsoft 登录，包含两条路径：
/// <list type="bullet">
///   <item>授权码 + PKCE + 本地回环回调（推荐）：直接在系统浏览器里复用已有的微软登录状态，
///   已登录的账户通常只需要点一次「继续」。</item>
///   <item>设备码（兜底）：浏览器不可用、或应用注册没登记回调地址时使用。</item>
/// </list>
/// 两条路径最后都汇合到 Xbox Live → XSTS → Minecraft Services。
/// The Azure application ID is supplied by the user so the launcher never ships
/// or impersonates somebody else's OAuth registration.
/// </summary>
public sealed class MicrosoftAuthService
{
    /// <summary>默认的回环回调端口，需要和 Azure 应用注册里的重定向 URI 对上。</summary>
    public const int DefaultRedirectPort = 48291;

    private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    private const string AuthorizeEndpoint = Authority + "/authorize";
    private const string DeviceCodeEndpoint = Authority + "/devicecode";
    private const string TokenEndpoint = Authority + "/token";
    private const string Scopes = "XboxLive.signin offline_access openid profile";

    /// <summary>游戏所有权（entitlements）查询，用来确认账号真的拥有 Java 版。</summary>
    private const string EntitlementsEndpoint = "https://api.minecraftservices.com/entitlements/mcstore";

    // 预检要阻止自动跳转才能读到 Location。
    private static readonly HttpClient ProbeClient = new(new HttpClientHandler { AllowAutoRedirect = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>拼出推荐的回环重定向 URI，形如 http://localhost:48291/。</summary>
    public static string BuildRedirectUri(int port)
    {
        var resolved = port is > 0 and < 65536 ? port : DefaultRedirectPort;
        return $"http://localhost:{resolved}/";
    }

    /// <summary>
    /// 依次尝试两种登记方式：
    /// 根路径（对应“重定向 URI 里只填 http://localhost”，此时端口被忽略，最省事），
    /// 以及精确的 /callback（对应把带端口的完整地址登记进 Azure）。
    /// </summary>
    public static IReadOnlyList<string> BuildRedirectCandidates(int port)
    {
        var resolved = port is > 0 and < 65536 ? port : DefaultRedirectPort;
        return [$"http://localhost:{resolved}/", $"http://localhost:{resolved}/callback"];
    }

    /// <summary>
    /// 找一个可用的回环端口；首选端口被占用时由系统分配。
    /// 重定向 URI 按 http://localhost 登记时端口不限，所以换端口不影响登录。
    /// </summary>
    public static int FindFreePort(int preferred)
    {
        foreach (var candidate in new[] { preferred, 0 })
        {
            try
            {
                var probe = new TcpListener(IPAddress.Loopback, candidate is > 0 and < 65536 ? candidate : 0);
                probe.Start();
                var port = ((IPEndPoint)probe.LocalEndpoint).Port;
                probe.Stop();
                return port;
            }
            catch (SocketException)
            {
                // 换下一个候选。
            }
        }

        return preferred is > 0 and < 65536 ? preferred : DefaultRedirectPort;
    }

    /// <summary>挑一个 Azure 已经认的回调地址；都不认时返回 NotRegistered。</summary>
    public async Task<MicrosoftRedirectSelection> ResolveRedirectAsync(string clientId, int port,
        CancellationToken cancellationToken = default)
    {
        var candidates = BuildRedirectCandidates(port);
        foreach (var candidate in candidates)
        {
            var check = await CheckRedirectUriAsync(clientId, candidate, cancellationToken).ConfigureAwait(false);
            switch (check.Status)
            {
                case MicrosoftRedirectStatus.Registered:
                    return new MicrosoftRedirectSelection(candidate, MicrosoftRedirectStatus.Registered);
                case MicrosoftRedirectStatus.Unknown when check.Detail is "AADSTS700016" or "unauthorized_client":
                    return new MicrosoftRedirectSelection(candidate, MicrosoftRedirectStatus.Unknown, check.Detail);
            }
        }

        return new MicrosoftRedirectSelection(candidates[0], MicrosoftRedirectStatus.NotRegistered);
    }

    /// <summary>
    /// 先用 prompt=none 发一次静默授权请求，看 Azure 认不认这个重定向 URI。
    /// consumers 授权端点会把请求转给 login.live.com，由后者校验 redirect_uri：
    /// 未登记时返回 “'redirect_uri' is not valid”，登记了则直接跳回回调地址（哪怕带着
    /// error=login_required）。这样可以在打开浏览器之前就把问题指出来，
    /// 而不是让用户看完错误页再等超时。
    /// </summary>
    public async Task<MicrosoftRedirectCheck> CheckRedirectUriAsync(string clientId, string redirectUri,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(redirectUri))
        {
            return new MicrosoftRedirectCheck(MicrosoftRedirectStatus.Unknown);
        }

        var (_, challenge) = CreatePkce();
        var url = AuthorizeEndpoint +
                  "?client_id=" + Uri.EscapeDataString(clientId) +
                  "&response_type=code" +
                  "&response_mode=query" +
                  "&redirect_uri=" + Uri.EscapeDataString(redirectUri) +
                  "&scope=" + Uri.EscapeDataString("openid profile") +
                  "&state=probe" +
                  "&prompt=none" +
                  "&code_challenge=" + Uri.EscapeDataString(challenge) +
                  "&code_challenge_method=S256";

        try
        {
            for (var hop = 0; hop < 6 && !string.IsNullOrEmpty(url); hop++)
            {
                using var response = await ProbeClient.GetAsync(url, cancellationToken).ConfigureAwait(false);
                var status = (int)response.StatusCode;
                var location = response.Headers.Location?.ToString();

                if (status is >= 300 and < 400 && !string.IsNullOrEmpty(location))
                {
                    var next = new Uri(new Uri(url), location).ToString();
                    if (IsSameCallback(next, redirectUri))
                    {
                        return new MicrosoftRedirectCheck(MicrosoftRedirectStatus.Registered);
                    }

                    url = next;
                    continue;
                }

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                var text = WebUtility.HtmlDecode(body);
                if (text.Contains("AADSTS50011", StringComparison.Ordinal) ||
                    text.Contains("'redirect_uri' is not valid", StringComparison.OrdinalIgnoreCase) ||
                    (text.Contains("redirect_uri", StringComparison.OrdinalIgnoreCase) &&
                     text.Contains("not valid", StringComparison.OrdinalIgnoreCase)))
                {
                    return new MicrosoftRedirectCheck(MicrosoftRedirectStatus.NotRegistered);
                }

                if (text.Contains("AADSTS700016", StringComparison.Ordinal) ||
                    text.Contains("AADSTS50059", StringComparison.Ordinal))
                {
                    return new MicrosoftRedirectCheck(MicrosoftRedirectStatus.Unknown, "AADSTS700016");
                }

                if (text.Contains("unauthorized_client", StringComparison.OrdinalIgnoreCase))
                {
                    return new MicrosoftRedirectCheck(MicrosoftRedirectStatus.Unknown, "unauthorized_client");
                }

                return new MicrosoftRedirectCheck(MicrosoftRedirectStatus.Unknown);
            }

            return new MicrosoftRedirectCheck(MicrosoftRedirectStatus.Unknown);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MicrosoftRedirectCheck(MicrosoftRedirectStatus.Unknown);
        }
        catch (Exception)
        {
            // 预检只是尽力而为，网络异常不应该挡住登录。
            return new MicrosoftRedirectCheck(MicrosoftRedirectStatus.Unknown);
        }
    }

    private static bool IsSameCallback(string candidate, string redirectUri)
    {
        if (!Uri.TryCreate(candidate, UriKind.Absolute, out var candidateUri) ||
            !Uri.TryCreate(redirectUri, UriKind.Absolute, out var expectedUri))
        {
            return false;
        }

        return string.Equals(candidateUri.Scheme, expectedUri.Scheme, StringComparison.OrdinalIgnoreCase) &&
               string.Equals(candidateUri.Host, expectedUri.Host, StringComparison.OrdinalIgnoreCase) &&
               candidateUri.Port == expectedUri.Port &&
               string.Equals(candidateUri.AbsolutePath.TrimEnd('/'), expectedUri.AbsolutePath.TrimEnd('/'),
                   StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 浏览器授权码 + PKCE 登录。redirectUri 必须是已登记的回环地址，
    /// 端口与路径都从它里面解析。
    /// </summary>
    public async Task<MicrosoftAccount> LoginWithBrowserAsync(string clientId, string redirectUri,
        Action<string> openBrowser, Action<string>? status = null, string? loginHint = null,
        bool switchAccount = false, Action<string>? redirectReady = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("还没有配置微软应用 ID。");
        }

        if (!Uri.TryCreate(redirectUri, UriKind.Absolute, out var redirect) ||
            redirect.Scheme != Uri.UriSchemeHttp ||
            (!string.Equals(redirect.Host, "localhost", StringComparison.OrdinalIgnoreCase) &&
             redirect.Host != "127.0.0.1"))
        {
            throw new InvalidOperationException($"回环重定向地址无效：{redirectUri}");
        }

        using var listener = new LoopbackOAuthListener(redirect.Port, redirect.AbsolutePath);
        listener.Start();
        try
        {
            var callbackUri = listener.RedirectUri;
            redirectReady?.Invoke(callbackUri);
            var (verifier, challenge) = CreatePkce();
            var state = CreateRandomToken(16);
            var authorizeUrl = BuildAuthorizeUrl(clientId, callbackUri, challenge, state, loginHint, switchAccount);

            status?.Invoke("已在浏览器中打开微软登录页…");
            try
            {
                openBrowser(authorizeUrl);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"无法打开浏览器（{ex.Message}）。请手动在浏览器中打开：{authorizeUrl}", ex);
            }

            var callback = await listener.WaitForCallbackAsync(TimeSpan.FromMinutes(5), cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrEmpty(callback.Error))
            {
                throw new InvalidOperationException(DescribeCallbackError(callback));
            }

            if (!string.Equals(callback.State, state, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("登录回调校验失败（state 不匹配），请重新登录。");
            }

            if (string.IsNullOrEmpty(callback.Code))
            {
                throw new InvalidOperationException("微软没有返回授权码，请重新登录。");
            }

            status?.Invoke("正在换取访问令牌…");
            var tokens = await ExchangeCodeAsync(clientId, callback.Code, callbackUri, verifier, cancellationToken)
                .ConfigureAwait(false);

            status?.Invoke("正在登录 Xbox Live…");
            return await CompleteMinecraftAsync(tokens.AccessToken, tokens.RefreshToken, status, cancellationToken,
                tokens.LoginHint).ConfigureAwait(false);
        }
        finally
        {
            listener.Dispose();
        }
    }

    public async Task<MicrosoftDeviceCode> StartDeviceCodeAsync(string clientId,
        CancellationToken cancellationToken = default)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = clientId,
            ["scope"] = "XboxLive.signin offline_access"
        });
        using var response = await DownloadHelper.HttpClient.PostAsync(DeviceCodeEndpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (body.Contains("AADSTS70002", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Azure 应用还没有启用“允许公共客户端流”。请打开该应用 → 身份验证 → 高级设置 → 把“允许公共客户端流”设为“是”，保存后再登录。");
            }

            if (body.Contains("AADSTS700016", StringComparison.Ordinal) ||
                body.Contains("AADSTS50059", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Azure 应用 ID 无效，或应用不在可用目录中。请确认复制的是“应用程序(客户端) ID”，并使用支持个人 Microsoft 账户的注册。");
            }

            throw new InvalidOperationException(ReadOAuthError(body) ?? $"微软设备码请求失败：{(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        return new MicrosoftDeviceCode(
            root.GetProperty("device_code").GetString() ?? "",
            root.GetProperty("user_code").GetString() ?? "",
            root.GetProperty("verification_uri").GetString() ?? "https://microsoft.com/link",
            root.TryGetProperty("interval", out var interval) ? interval.GetInt32() : 5,
            root.TryGetProperty("expires_in", out var expires) ? expires.GetInt32() : 900,
            root.TryGetProperty("message", out var message) ? message.GetString() ?? "" : "");
    }

    public async Task<MicrosoftAccount> CompleteAsync(string clientId, MicrosoftDeviceCode deviceCode,
        Action<string>? status, CancellationToken cancellationToken = default)
    {
        status?.Invoke("等待在浏览器中完成微软登录…");
        var deadline = DateTime.UtcNow.AddSeconds(deviceCode.ExpiresInSeconds);
        var interval = Math.Max(2, deviceCode.IntervalSeconds);
        string accessToken;
        string refreshToken;
        while (true)
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("微软登录已超时，请重新生成设备码。");
            }

            await Task.Delay(TimeSpan.FromSeconds(interval), cancellationToken);
            using var content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = deviceCode.DeviceCode
            });
            using var response = await DownloadHelper.HttpClient.PostAsync(TokenEndpoint, content, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                using var document = JsonDocument.Parse(body);
                accessToken = document.RootElement.GetProperty("access_token").GetString() ?? "";
                refreshToken = document.RootElement.TryGetProperty("refresh_token", out var refresh)
                    ? refresh.GetString() ?? ""
                    : "";
                break;
            }

            var error = ReadOAuthErrorCode(body);
            if (error == "authorization_pending")
            {
                status?.Invoke("等待确认登录…");
                continue;
            }

            if (error == "slow_down")
            {
                interval += 5;
                status?.Invoke("微软要求降低轮询频率…");
                continue;
            }

            throw new InvalidOperationException(ReadOAuthError(body) ?? $"微软登录失败：{(int)response.StatusCode}");
        }

        status?.Invoke("正在登录 Xbox Live…");
        return await CompleteMinecraftAsync(accessToken, refreshToken, status, cancellationToken);
    }

    /// <summary>
    /// Uses a stored OAuth refresh token to obtain a fresh Minecraft session.
    /// PCL2 also treats the refresh token as the long-lived account credential.
    /// </summary>
    public async Task<MicrosoftAccount> RefreshAsync(string clientId, string refreshToken,
        Action<string>? status = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(refreshToken))
        {
            throw new InvalidOperationException("没有可用的微软刷新令牌，请在账户页重新登录。");
        }

        status?.Invoke("正在刷新 Microsoft 登录…");
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = "XboxLive.signin offline_access"
        });
        using var response = await DownloadHelper.HttpClient.PostAsync(TokenEndpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var error = ReadOAuthErrorCode(body);
            if (error is "invalid_grant" or "interaction_required")
            {
                throw new InvalidOperationException("微软登录已失效，请在账户页重新登录。");
            }

            throw new InvalidOperationException(ReadOAuthError(body) ?? $"微软登录刷新失败：{(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = root.GetProperty("access_token").GetString() ?? "";
        var newRefreshToken = root.TryGetProperty("refresh_token", out var refresh)
            ? refresh.GetString() ?? refreshToken
            : refreshToken;
        var loginHint = root.TryGetProperty("id_token", out var idToken)
            ? ReadPreferredUsername(idToken.GetString())
            : null;
        return await CompleteMinecraftAsync(accessToken, newRefreshToken, status, cancellationToken, loginHint);
    }

    private static string BuildAuthorizeUrl(string clientId, string redirectUri, string challenge, string state,
        string? loginHint, bool switchAccount)
    {
        var parameters = new List<(string Key, string Value)>
        {
            ("client_id", clientId),
            ("response_type", "code"),
            ("response_mode", "query"),
            ("redirect_uri", redirectUri),
            ("scope", Scopes),
            ("code_challenge", challenge),
            ("code_challenge_method", "S256"),
            ("state", state)
        };

        if (switchAccount)
        {
            parameters.Add(("prompt", "select_account"));
        }

        if (!string.IsNullOrWhiteSpace(loginHint))
        {
            parameters.Add(("login_hint", loginHint.Trim()));
        }

        var query = string.Join('&', parameters.Select(p => $"{p.Key}={Uri.EscapeDataString(p.Value)}"));
        return $"{AuthorizeEndpoint}?{query}";
    }

    private static async Task<(string AccessToken, string RefreshToken, string? LoginHint)> ExchangeCodeAsync(
        string clientId, string code, string redirectUri, string verifier, CancellationToken cancellationToken)
    {
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["client_id"] = clientId,
            ["code"] = code,
            ["redirect_uri"] = redirectUri,
            ["code_verifier"] = verifier,
            ["scope"] = Scopes
        });
        using var response = await DownloadHelper.HttpClient.PostAsync(TokenEndpoint, content, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (body.Contains("AADSTS50011", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"重定向 URI 与 Azure 应用注册不一致：{redirectUri}。请把这个地址添加到“身份验证 → 移动和桌面应用程序 → 重定向 URI”。");
            }

            if (body.Contains("AADSTS7000218", StringComparison.Ordinal) ||
                body.Contains("AADSTS70002", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Azure 应用还没有启用“允许公共客户端流”，请在应用注册 → 身份验证 → 高级设置中打开。");
            }

            if (body.Contains("invalid_grant", StringComparison.Ordinal))
            {
                throw new InvalidOperationException("授权码已过期或被使用过，请重新登录。");
            }

            throw new InvalidOperationException(ReadOAuthError(body) ?? $"微软令牌交换失败：{(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var accessToken = root.GetProperty("access_token").GetString() ?? "";
        var refreshToken = root.TryGetProperty("refresh_token", out var refresh)
            ? refresh.GetString() ?? ""
            : "";
        var loginHint = root.TryGetProperty("id_token", out var idToken)
            ? ReadPreferredUsername(idToken.GetString())
            : null;
        return (accessToken, refreshToken, loginHint);
    }

    private static async Task<MicrosoftAccount> CompleteMinecraftAsync(string accessToken, string refreshToken,
        Action<string>? status, CancellationToken cancellationToken, string? loginHint = null)
    {
        status?.Invoke("正在登录 Xbox Live…");
        var xblToken = await XboxAuthenticateAsync(accessToken, cancellationToken);
        var (xstsToken, uhs) = await XstsAuthorizeAsync(xblToken, cancellationToken);
        status?.Invoke("正在登录 Minecraft…");
        var minecraftToken = await MinecraftLoginAsync(uhs, xstsToken, cancellationToken);

        status?.Invoke("正在校验游戏所有权…");
        var entitlement = await CheckGameEntitlementAsync(minecraftToken, cancellationToken);
        if (entitlement == false)
        {
            throw new InvalidOperationException(
                "该微软账户没有 Minecraft: Java Edition 的游戏所有权（Mojang 未返回 game_minecraft 授权）。" +
                "这个账号不能用于正版联机，请换一个已购买的账号；本地单人游戏仍可使用离线账户。");
        }

        status?.Invoke("正在读取 Minecraft 档案…");
        var profile = await MinecraftProfileAsync(minecraftToken, cancellationToken);
        return new MicrosoftAccount(profile.Name, profile.Uuid, minecraftToken, refreshToken, profile.SkinUrl,
            loginHint);
    }

    private static async Task<string> XboxAuthenticateAsync(string accessToken, CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                AuthMethod = "RPS",
                SiteName = "user.auth.xboxlive.com",
                RpsTicket = "d=" + accessToken
            },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        };
        using var response = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"Xbox Live 认证失败：{(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("Token").GetString()
               ?? throw new InvalidOperationException("Xbox Live 没有返回 Token。");
    }

    private static async Task<(string Token, string Uhs)> XstsAuthorizeAsync(string xblToken,
        CancellationToken cancellationToken)
    {
        var payload = new
        {
            Properties = new
            {
                SandboxId = "RETAIL",
                UserTokens = new[] { xblToken }
            },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        };
        using var response = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            using var error = JsonDocument.Parse(body);
            var xerr = error.RootElement.TryGetProperty("XErr", out var xerrValue) ? xerrValue.GetInt64() : 0;
            var reason = xerr switch
            {
                2148916233 => "该微软账户还没有 Xbox 档案。",
                2148916235 => "该账户所在地区不支持 Xbox Live。",
                2148916236 => "需要先在 Xbox 页面完成成人验证。",
                2148916237 => "需要先在 Xbox 页面完成成人验证。",
                2148916238 => "未成年账户需要先加入家庭组。",
                _ => $"XSTS 认证失败（XErr {xerr}）。"
            };
            throw new InvalidOperationException(reason);
        }

        using var document = JsonDocument.Parse(body);
        var token = document.RootElement.GetProperty("Token").GetString()
                    ?? throw new InvalidOperationException("XSTS 没有返回 Token。");
        var uhs = document.RootElement.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()
                  ?? "";
        return (token, uhs);
    }

    private static async Task<string> MinecraftLoginAsync(string uhs, string xstsToken,
        CancellationToken cancellationToken)
    {
        var payload = new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" };
        using var response = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            payload, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Forbidden)
            {
                throw new InvalidOperationException(
                    "Minecraft 登录失败：403。你的 Azure 应用还没有通过 Minecraft Services 的应用注册审核（Invalid app registration）。" +
                    "请在 https://help.minecraft.net/hc/en-us/articles/16254801392141 提交 Application (client) ID 审核，通过后再登录。");
            }

            throw new InvalidOperationException($"Minecraft 登录失败：{(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("access_token").GetString()
               ?? throw new InvalidOperationException("Minecraft 没有返回访问令牌。");
    }

    /// <summary>用 Minecraft 令牌读取账号档案（含皮肤地址）；令牌失效时返回 null。</summary>
    public static async Task<(string Name, string Uuid, string? SkinUrl)?> TryGetProfileAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return null;
        }

        try
        {
            return await MinecraftProfileAsync(accessToken, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(string Name, string Uuid, string? SkinUrl)> MinecraftProfileAsync(string accessToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await DownloadHelper.HttpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            throw new InvalidOperationException("该微软账户没有 Minecraft: Java Edition 游戏所有权。");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException($"读取 Minecraft 档案失败：{(int)response.StatusCode}");
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        var name = root.GetProperty("name").GetString() ?? "";
        var uuid = root.GetProperty("id").GetString() ?? "";
        string? skin = null;
        if (root.TryGetProperty("skins", out var skins) && skins.ValueKind == JsonValueKind.Array &&
            skins.GetArrayLength() > 0 && skins[0].TryGetProperty("url", out var url))
        {
            skin = url.GetString();
        }

        return (name, uuid, skin);
    }

    /// <summary>
    /// 查询账号是否真的拥有 Minecraft: Java Edition。
    /// 返回 true = 拥有，false = 明确没有，null = 无法判断（网络/接口异常时不阻塞登录）。
    /// </summary>
    public static async Task<bool?> CheckGameEntitlementAsync(string accessToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accessToken))
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, EntitlementsEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        try
        {
            using var response = await DownloadHelper.HttpClient.SendAsync(request, cancellationToken);
            if (response.StatusCode == HttpStatusCode.Unauthorized)
            {
                throw new InvalidOperationException("Minecraft 登录状态已失效，请在账户页重新登录。");
            }

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ParseEntitlements(body);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// 解析 /entitlements/mcstore 的响应。拥有 Java 版时 items 里会出现
    /// game_minecraft（部分账号只有 product_minecraft），两者都算拥有。
    /// </summary>
    public static bool? ParseEntitlements(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("items", out var items) ||
                items.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            foreach (var item in items.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("name", out var name) ||
                    name.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                var value = name.GetString();
                if (string.Equals(value, "game_minecraft", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(value, "product_minecraft", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static async Task<HttpResponseMessage> PostJsonAsync(string url, object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await DownloadHelper.HttpClient.PostAsync(url, content, cancellationToken);
    }

    private static (string Verifier, string Challenge) CreatePkce()
    {
        var verifier = ToBase64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = ToBase64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return (verifier, challenge);
    }

    private static string CreateRandomToken(int bytes) => ToBase64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string ToBase64Url(byte[] data) =>
        Convert.ToBase64String(data).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string? ReadPreferredUsername(string? idToken)
    {
        if (string.IsNullOrWhiteSpace(idToken))
        {
            return null;
        }

        var parts = idToken.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(payload.Length + (4 - payload.Length % 4) % 4, '=');
            using var document = JsonDocument.Parse(Convert.FromBase64String(payload));
            foreach (var name in new[] { "preferred_username", "email", "upn" })
            {
                if (document.RootElement.TryGetProperty(name, out var value) &&
                    value.ValueKind == JsonValueKind.String)
                {
                    return value.GetString();
                }
            }
        }
        catch
        {
            // id_token 只是用来记住上次的账户名，解析失败不影响登录。
        }

        return null;
    }

    private static string DescribeCallbackError(OAuthCallback callback)
    {
        var code = callback.Error ?? "";
        var description = callback.ErrorDescription ?? "";
        if (string.Equals(code, "access_denied", StringComparison.OrdinalIgnoreCase))
        {
            return description.Contains("cancel", StringComparison.OrdinalIgnoreCase)
                ? "登录已取消。"
                : "微软账户拒绝了本次登录授权。";
        }

        if (code.StartsWith("AADSTS", StringComparison.Ordinal))
        {
            return $"微软返回错误 {code}：{description}";
        }

        return string.IsNullOrWhiteSpace(description) ? $"微软登录失败：{code}" : $"微软登录失败：{description}";
    }

    private static string? ReadOAuthErrorCode(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? ReadOAuthError(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var description = document.RootElement.TryGetProperty("error_description", out var desc)
                ? desc.GetString()
                : null;
            var code = document.RootElement.TryGetProperty("error", out var error) ? error.GetString() : null;
            return description ?? code;
        }
        catch
        {
            return null;
        }
    }
}
