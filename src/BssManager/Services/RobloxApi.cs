using System.Net;
using System.Net.Http;
using System.Text.Json;

namespace BssManager.Services;

/// <summary>Who a captured login token belongs to.</summary>
public record RobloxIdentity(long UserId, string Username, string DisplayName);

/// <summary>
/// The only place this app talks to Roblox. It asks one question -- "whose
/// account is this token, and does it still work?" -- because a token that has
/// been revoked looks exactly like a working one until something uses it.
/// </summary>
public static class RobloxApi
{
    private const string AuthenticatedUrl = "https://users.roblox.com/v1/users/authenticated";

    /// <summary>
    /// UseCookies is off so the token can be attached per request and nothing
    /// is retained in a shared container between accounts.
    /// </summary>
    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    /// <summary>
    /// Returns the account the token belongs to, or null if Roblox rejects it.
    /// The token is never logged, here or anywhere else.
    /// </summary>
    public static async Task<RobloxIdentity?> WhoAmIAsync(string cookie, CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(cookie)) return null;

        using var request = new HttpRequestMessage(HttpMethod.Get, AuthenticatedUrl);
        request.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cookie}");

        // Roblox serves a challenge page to clients that do not look like a
        // browser, and the challenge is not something this call can solve.
        request.Headers.TryAddWithoutValidation("User-Agent",
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");

        try
        {
            using var response = await Client.SendAsync(request, token);

            if (response.StatusCode == HttpStatusCode.Unauthorized) return null;
            if (!response.IsSuccessStatusCode)
            {
                Log.Write($"roblox whoami returned {(int)response.StatusCode}");
                return null;
            }

            await using var stream = await response.Content.ReadAsStreamAsync(token);
            using var json = await JsonDocument.ParseAsync(stream, cancellationToken: token);

            var root = json.RootElement;
            var id = root.GetProperty("id").GetInt64();
            var name = root.GetProperty("name").GetString() ?? "";
            var display = root.TryGetProperty("displayName", out var d) ? d.GetString() ?? name : name;

            return new RobloxIdentity(id, name, display);
        }
        catch (Exception ex)
        {
            Log.Write($"roblox whoami failed: {ex.Message}");
            return null;
        }
    }
}
