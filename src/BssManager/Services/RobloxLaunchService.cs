using System.Net;
using System.Net.Http;
using BssManager.Models;

namespace BssManager.Services;

/// <summary>
/// Turns a stored login token into a one-shot URL that signs Roblox in and joins
/// a game.
///
/// Roblox never lets a saved token launch the client directly. The token buys a
/// short-lived, single-use *authentication ticket*, and the ticket is what goes
/// into the roblox-player launch URL. That indirection is deliberate on their
/// side and useful on ours: the thing that travels into the alt's session
/// expires within about a minute and cannot be replayed.
///
/// Getting the ticket needs a CSRF token, which Roblox only hands out by
/// rejecting a request first -- so the 403 below is the expected path, not an
/// error.
/// </summary>
public class RobloxLaunchService
{
    /// <summary>Bee Swarm Simulator, by Onett. Verified against the Roblox games API.</summary>
    public const long BeeSwarmPlaceId = 1537690962;

    private const string TicketUrl = "https://auth.roblox.com/v1/authentication-ticket/";
    private const string PlaceLauncher = "https://assetgame.roblox.com/game/PlaceLauncher.ashx";

    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly HttpClient Client = new(new HttpClientHandler
    {
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.All
    })
    {
        Timeout = TimeSpan.FromSeconds(25)
    };

    /// <summary>
    /// Builds the launch URL for an account. Returns null with a reason when
    /// Roblox will not issue a ticket -- almost always a token that has expired.
    /// </summary>
    public async Task<(string? url, string message)> BuildLaunchUrlAsync(
        string cookie, long placeId, PrivateServerLink? privateServer = null,
        CancellationToken token = default)
    {
        if (string.IsNullOrWhiteSpace(cookie))
            return (null, "No stored login for that account. Add it again.");

        var (ticket, message) = await GetTicketAsync(cookie, token);
        if (ticket is null) return (null, message);

        // The link names its own game, and that is more trustworthy than the
        // configured place id: joining a private server of a different game
        // would silently do nothing useful.
        return (BuildUrl(ticket, privateServer?.PlaceId ?? placeId, privateServer?.LinkCode), "");
    }

    /// <summary>
    /// Assembles the launch URL around a ticket. Separated from fetching one so
    /// the format can be checked without a live account.
    ///
    /// With a link code the request becomes RequestPrivateGame, which is how the
    /// website joins a private server: the code is the invitation, and Roblox
    /// resolves it to an actual server on its side.
    /// </summary>
    public static string BuildUrl(string ticket, long placeId, string? privateServerCode = null)
    {
        // Roblox uses this to tie the launch to a browser session. Nothing
        // checks it against anything, but it has to be present and identical in
        // the launcher URL and the outer one.
        var tracker = Random.Shared.NextInt64(1_000_000_000L, 9_999_999_999L);

        var placeLauncherUrl = string.IsNullOrWhiteSpace(privateServerCode)
            ? $"{PlaceLauncher}?request=RequestGame&browserTrackerId={tracker}" +
              $"&placeId={placeId}&isPlayTogetherGame=false"

            // accessCode is sent empty on purpose: the site sends the parameter
            // either way, and the code in linkCode is what identifies the server.
            : $"{PlaceLauncher}?request=RequestPrivateGame&browserTrackerId={tracker}" +
              $"&placeId={placeId}&accessCode=&linkCode={privateServerCode}" +
              "&isPlayTogetherGame=false";

        var launchTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        // Order matters to the client, and the nested URL has to be escaped:
        // its own separators would otherwise be read as fields of the outer one.
        return
            "roblox-player:1" +
            "+launchmode:play" +
            $"+gameinfo:{ticket}" +
            $"+launchtime:{launchTime}" +
            $"+placelauncherurl:{Uri.EscapeDataString(placeLauncherUrl)}" +
            $"+browsertrackerid:{tracker}" +
            "+robloxLocale:en_us" +
            "+gameLocale:en_us";
    }

    /// <summary>
    /// Trades the token for an authentication ticket. The first request is
    /// expected to fail with 403 and hand back the CSRF token to retry with.
    /// </summary>
    private static async Task<(string? ticket, string message)> GetTicketAsync(
        string cookie, CancellationToken token)
    {
        var (response, csrf) = await PostAsync(cookie, csrf: null, token);

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized)
                return (null, "Roblox rejected the stored login. Sign in to that account again.");

            // 403 with a token is the handshake; 403 without one is a refusal.
            if (response.StatusCode == HttpStatusCode.Forbidden && csrf is null)
                return (null, "Roblox refused the request and did not issue a CSRF token.");

            if (response.IsSuccessStatusCode)
                return ReadTicket(response);
        }

        using var retry = (await PostAsync(cookie, csrf, token)).response;

        if (retry.StatusCode == HttpStatusCode.Unauthorized)
            return (null, "Roblox rejected the stored login. Sign in to that account again.");

        if (!retry.IsSuccessStatusCode)
        {
            Log.Write($"authentication ticket failed: {(int)retry.StatusCode}");
            return (null, $"Roblox would not issue a launch ticket (HTTP {(int)retry.StatusCode}).");
        }

        return ReadTicket(retry);
    }

    private static async Task<(HttpResponseMessage response, string? csrf)> PostAsync(
        string cookie, string? csrf, CancellationToken token)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, TicketUrl)
        {
            Content = new StringContent("")
        };

        request.Headers.TryAddWithoutValidation("Cookie", $".ROBLOSECURITY={cookie}");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        // Roblox rejects a ticket request that does not look like it came from
        // its own site.
        request.Headers.TryAddWithoutValidation("Referer", "https://www.roblox.com/");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.roblox.com");

        if (csrf is not null) request.Headers.TryAddWithoutValidation("X-CSRF-TOKEN", csrf);

        var response = await Client.SendAsync(request, token);

        var issued = response.Headers.TryGetValues("x-csrf-token", out var values)
            ? values.FirstOrDefault()
            : null;

        return (response, issued);
    }

    private static (string? ticket, string message) ReadTicket(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("rbx-authentication-ticket", out var values))
        {
            var ticket = values.FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(ticket)) return (ticket, "");
        }

        return (null, "Roblox accepted the request but returned no launch ticket.");
    }
}
