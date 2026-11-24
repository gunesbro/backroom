namespace Backroom.BuildingBlocks.Presentation.Cors;

/// <summary>What counts as a valid configured web origin.</summary>
internal static class WebOriginRules
{
    /// <summary>
    /// True for an exact origin: <c>https</c> (or <c>http</c> on a loopback host, including <c>*.localhost</c>) or
    /// <c>tauri://localhost</c> (the desktop shell); a host without wildcards, no user info, path, query or fragment.
    /// </summary>
    public static bool IsValid(string? origin)
    {
        if (string.IsNullOrWhiteSpace(origin)
            || origin.Contains('*', StringComparison.Ordinal)
            || !Uri.TryCreate(origin, UriKind.Absolute, out var uri)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query)
            || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrEmpty(uri.Host)
            || !string.Equals(origin, uri.GetLeftPart(UriPartial.Authority), StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return uri.Scheme switch
        {
            "https" => true,
            "http" => IsLoopback(uri),
            "tauri" => string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase),
            _ => false,
        };
    }

    private static bool IsLoopback(Uri uri) =>
        uri.IsLoopback
        || string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
        || uri.Host.EndsWith(".localhost", StringComparison.OrdinalIgnoreCase);
}
