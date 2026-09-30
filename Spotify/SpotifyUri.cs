using System.Text.RegularExpressions;

namespace LoupixDeck.Plugin.SpotifyPremium.Spotify;

/// <summary>
/// Turns what a user pastes into a command parameter — a bare ID, a
/// <c>spotify:{type}:{id}</c> URI or an <c>open.spotify.com</c> share link —
/// into a canonical Spotify URI.
/// </summary>
internal static partial class SpotifyUri
{
    /// <summary>
    /// Returns <c>spotify:{type}:{id}</c> for <paramref name="input"/>, or
    /// <c>null</c> when it is empty or names a different item type.
    /// </summary>
    public static string? Normalize(string? input, string type)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var value = input.Trim();

        var uri = UriPattern().Match(value);
        if (uri.Success)
            return uri.Groups["type"].Value == type ? $"spotify:{type}:{uri.Groups["id"].Value}" : null;

        // Share links look like https://open.spotify.com/intl-de/album/{id}?si=...
        var link = LinkPattern().Match(value);
        if (link.Success)
            return link.Groups["type"].Value == type ? $"spotify:{type}:{link.Groups["id"].Value}" : null;

        return IdPattern().IsMatch(value) ? $"spotify:{type}:{value}" : null;
    }

    [GeneratedRegex(@"^spotify:(?<type>[a-z]+):(?<id>[A-Za-z0-9]+)$")]
    private static partial Regex UriPattern();

    [GeneratedRegex(@"^https?://open\.spotify\.com/(?:intl-[A-Za-z-]+/)?(?<type>[a-z]+)/(?<id>[A-Za-z0-9]+)")]
    private static partial Regex LinkPattern();

    [GeneratedRegex(@"^[A-Za-z0-9]{22}$")]
    private static partial Regex IdPattern();
}
