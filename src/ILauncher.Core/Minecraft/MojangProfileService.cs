using System.Net.Http.Headers;
using System.Text.Json;

namespace ILauncher.Core.Minecraft;

public sealed record MojangProfileInfo(
    string Name,
    string Uuid,
    string? SkinUrl,
    string? CapeUrl,
    string SkinModel);

/// <summary>Read-only Mojang profile lookup. No credentials are stored or required.</summary>
public sealed class MojangProfileService
{
    private static readonly HttpClient Http = CreateClient();

    public async Task<MojangProfileInfo?> LookupAsync(string input, CancellationToken cancellationToken = default)
    {
        input = input.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            return null;
        }

        string? uuid = null;
        string? name = null;

        if (IsUuid(input))
        {
            uuid = NormalizeUuid(input);
        }
        else
        {
            var profile = await LookupByNameAsync(input, cancellationToken);
            if (profile == null)
            {
                return null;
            }

            uuid = profile.Value.Uuid;
            name = profile.Value.Name;
        }

        var sessionUrl = $"https://sessionserver.mojang.com/session/minecraft/profile/{uuid}";
        using var response = await Http.GetAsync(sessionUrl, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return name == null ? null : new MojangProfileInfo(name, uuid, null, null, "Steve");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        var root = document.RootElement;
        name = GetString(root, "name") ?? name ?? input;
        var id = GetString(root, "id") ?? uuid;
        string? skinUrl = null;
        string? capeUrl = null;
        var model = "Steve";

        if (root.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Array)
        {
            foreach (var property in properties.EnumerateArray())
            {
                if (!string.Equals(GetString(property, "name"), "textures", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = GetString(property, "value");
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                try
                {
                    var json = JsonSerializer.Deserialize<JsonElement>(Convert.FromBase64String(value));
                    if (json.TryGetProperty("textures", out var textures))
                    {
                        if (textures.TryGetProperty("SKIN", out var skin))
                        {
                            skinUrl = GetString(skin, "url");
                            if (skin.TryGetProperty("metadata", out var metadata))
                            {
                                model = string.Equals(GetString(metadata, "model"), "slim", StringComparison.OrdinalIgnoreCase)
                                    ? "Alex"
                                    : "Steve";
                            }
                        }

                        if (textures.TryGetProperty("CAPE", out var cape))
                        {
                            capeUrl = GetString(cape, "url");
                        }
                    }
                }
                catch
                {
                    // Ignore malformed texture properties.
                }
            }
        }

        return new MojangProfileInfo(name ?? input, NormalizeUuid(id), skinUrl, capeUrl, model);
    }

    public async Task<byte[]?> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await Http.GetAsync(url, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsByteArrayAsync(cancellationToken);
        }
        catch
        {
            return null;
        }
    }

    private static async Task<(string Uuid, string Name)?> LookupByNameAsync(string name, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await Http.GetAsync(
                $"https://api.mojang.com/users/profiles/minecraft/{Uri.EscapeDataString(name)}", cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var root = document.RootElement;
            var id = GetString(root, "id");
            var profileName = GetString(root, "name");
            return id == null ? null : (NormalizeUuid(id), profileName ?? name);
        }
        catch
        {
            return null;
        }
    }

    private static bool IsUuid(string value)
    {
        var clean = value.Replace("-", "", StringComparison.Ordinal);
        return clean.Length == 32 && clean.All(Uri.IsHexDigit);
    }

    public static string NormalizeUuid(string uuid)
    {
        var clean = uuid.Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        if (clean.Length != 32)
        {
            return uuid;
        }

        return $"{clean[..8]}-{clean[8..12]}-{clean[12..16]}-{clean[16..20]}-{clean[20..]}";
    }

    private static string? GetString(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ILauncher/1.1 (skin lookup)");
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }
}
