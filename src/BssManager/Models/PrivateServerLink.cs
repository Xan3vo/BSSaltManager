using System.Text.RegularExpressions;
using System.Web;

namespace BssManager.Models;

/// <summary>
/// A private server link, in the long-standing form:
///
///   https://www.roblox.com/games/1537690962/Bee-Swarm-Simulator?privateServerLinkCode=284992746131...
///
/// The code in the query is what actually joins the server; the place id says
/// which game it belongs to. Roblox also hands out short share links now
/// (roblox.com/share?code=...&amp;type=Server), which carry no code that can be
/// used directly -- they have to be resolved by a signed-in request first. Those
/// are refused with an explanation rather than half-accepted.
/// </summary>
public sealed partial record PrivateServerLink(long PlaceId, string LinkCode, string Url)
{
    /// <summary>Codes are long opaque strings; this is deliberately loose about their shape.</summary>
    [GeneratedRegex(@"^[A-Za-z0-9_-]{8,128}$")]
    private static partial Regex CodePattern();

    [GeneratedRegex(@"/games/(\d+)(?:/([^/?#]+))?", RegexOptions.IgnoreCase)]
    private static partial Regex PlaceIdPattern();

    /// <summary>
    /// Reads a pasted link. An empty string is a valid answer meaning "no
    /// private server", and comes back as null with no problem.
    /// </summary>
    public static bool TryParse(
        string? input, long fallbackPlaceId, out PrivateServerLink? link, out string problem)
    {
        link = null;
        problem = "";

        var text = input?.Trim() ?? "";
        if (text.Length == 0) return true;

        // A bare code, pasted out of a link by hand.
        if (!text.Contains('/') && CodePattern().IsMatch(text))
        {
            link = Create(fallbackPlaceId, text);
            return true;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            problem = "That is not a link. Paste the whole roblox.com address for the private server.";
            return false;
        }

        if (!uri.Host.EndsWith("roblox.com", StringComparison.OrdinalIgnoreCase))
        {
            problem = "That link is not a roblox.com address.";
            return false;
        }

        var query = HttpUtility.ParseQueryString(uri.Query);
        var code = query["privateServerLinkCode"];

        if (string.IsNullOrWhiteSpace(code))
        {
            // The short share link is the common wrong paste, and it is worth
            // saying so precisely: it looks like a private server link and is
            // not one.
            problem = uri.AbsolutePath.Contains("/share", StringComparison.OrdinalIgnoreCase)
                ? "That is a share link, which does not carry a usable code. Open it in a browser and copy the roblox.com/games/... address it lands on."
                : "That link has no privateServerLinkCode in it. Open the private server, copy its link, and paste the whole thing.";
            return false;
        }

        code = code.Trim();
        if (!CodePattern().IsMatch(code))
        {
            problem = "The privateServerLinkCode in that link does not look right.";
            return false;
        }

        var placeMatch = PlaceIdPattern().Match(uri.AbsolutePath);
        var placeId = placeMatch.Success && long.TryParse(placeMatch.Groups[1].Value, out var parsed)
            ? parsed
            : fallbackPlaceId;

        // Roblox ignores the game-name segment, but it is part of the link you
        // pasted, so it is part of the link you get back.
        var slug = placeMatch.Success ? placeMatch.Groups[2].Value : "";

        link = Create(placeId, code, slug);
        return true;
    }

    private static PrivateServerLink Create(long placeId, string code, string slug = "")
    {
        var path = slug.Length > 0
            ? $"{placeId}/{slug}"
            : placeId.ToString(System.Globalization.CultureInfo.InvariantCulture);

        return new(placeId, code,
            $"https://www.roblox.com/games/{path}?privateServerLinkCode={code}");
    }

    /// <summary>Short form for the alt's card, where the full link would swamp the row.</summary>
    public string Summary => LinkCode.Length <= 12
        ? $"private server {LinkCode}"
        : $"private server {LinkCode[..6]}...{LinkCode[^4..]}";
}
