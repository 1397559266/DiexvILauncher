using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace ILauncher.Core.Services;

public sealed record MicrosoftDeviceCode(string DeviceCode, string UserCode, string VerificationUri,
    int IntervalSeconds, int ExpiresInSeconds, string Message);

public sealed record MicrosoftAccount(string Name, string Uuid, string AccessToken, string RefreshToken, string? SkinUrl);

/// <summary>
/// Microsoft device-code login followed by Xbox Live, XSTS and Minecraft Services.
/// The Azure application ID is supplied by the user so the launcher never ships
/// or impersonates somebody else's OAuth registration.
/// </summary>
public sealed class MicrosoftAuthService
{
    private const string DeviceCodeEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenEndpoint = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";

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
        return await CompleteMinecraftAsync(accessToken, newRefreshToken, status, cancellationToken);
    }

    private static async Task<MicrosoftAccount> CompleteMinecraftAsync(string accessToken, string refreshToken,
        Action<string>? status, CancellationToken cancellationToken)
    {
        status?.Invoke("正在登录 Xbox Live…");
        var xblToken = await XboxAuthenticateAsync(accessToken, cancellationToken);
        var (xstsToken, uhs) = await XstsAuthorizeAsync(xblToken, cancellationToken);
        status?.Invoke("正在登录 Minecraft…");
        var minecraftToken = await MinecraftLoginAsync(uhs, xstsToken, cancellationToken);
        status?.Invoke("正在读取 Minecraft 档案…");
        var profile = await MinecraftProfileAsync(minecraftToken, cancellationToken);
        return new MicrosoftAccount(profile.Name, profile.Uuid, minecraftToken, refreshToken, profile.SkinUrl);
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

    private static async Task<HttpResponseMessage> PostJsonAsync(string url, object payload,
        CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        return await DownloadHelper.HttpClient.PostAsync(url, content, cancellationToken);
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
