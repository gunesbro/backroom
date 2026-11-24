namespace Backroom.BuildingBlocks.Presentation.Cors;

/// <summary>What counts as a valid configured web origin.</summary>
internal static class WebOriginRules
{
    /// <summary>
    /// True for an exact, lower-case origin: <c>https</c>; <c>tauri://localhost</c> and <c>http://tauri.localhost</c> (the desktop
    /// shell); and, only in Development, <c>http</c> on <c>localhost</c>, <c>127.0.0.1</c> or <c>[::1]</c>. No wildcards, user info,
    /// path, query, fragment or default port (<c>https://x:443</c>).
    /// </summary>
    public static bool IsValid(string? origin, bool development)
    {
        if (string.IsNullOrWhiteSpace(origin)
            || origin.Contains('*', StringComparison.Ordinal)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrEmpty(uri.Host)
            || !string.Equals(uri.Host, uri.IdnHost, StringComparison.Ordinal)
            // Exactly what a browser sends: lower case, punycode host, no default port.
            || !string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.Ordinal)
            || !string.Equals(origin, origin.ToLowerInvariant(), StringComparison.Ordinal))
        {
            return false;
        }

        return uri.Scheme switch
        {
            "https" => true,
            "http" => string.Equals(uri.Host, "tauri.localhost", StringComparison.Ordinal)
                || (development && uri.Host is "localhost" or "127.0.0.1" or "[::1]"),
            "tauri" => string.Equals(uri.Host, "localhost", StringComparison.Ordinal),
            _ => false,
        };
    }
}
