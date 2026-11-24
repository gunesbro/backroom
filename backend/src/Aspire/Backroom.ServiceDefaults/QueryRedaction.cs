namespace Backroom.ServiceDefaults;

/// <summary>
/// Hides secrets in a URL's query string before it is written to traces. A SignalR WebSocket cannot carry an
/// Authorization header, so its access token travels as <c>access_token</c> in the query.
/// </summary>
public static class QueryRedaction
{
    public const string Placeholder = "[REDACTED]";

    private static readonly string[] s_secretParameters = ["access_token"];

    /// <summary>The query (with or without the leading <c>?</c>) with the values of secret parameters replaced.</summary>
    public static string Redact(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Length == 0)
        {
            return query;
        }

        var parts = query.Split('&');
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            var equals = part.IndexOf('=', StringComparison.Ordinal);
            var name = (equals < 0 ? part : part[..equals]).TrimStart('?');
            if (IsSecret(name))
            {
                parts[i] = string.Concat(part.AsSpan(0, equals < 0 ? part.Length : equals), "=", Placeholder);
            }
        }

        return string.Join('&', parts);
    }

    private static bool IsSecret(string name) =>
        Array.Exists(s_secretParameters, secret => string.Equals(secret, Uri.UnescapeDataString(name), StringComparison.OrdinalIgnoreCase));
}
