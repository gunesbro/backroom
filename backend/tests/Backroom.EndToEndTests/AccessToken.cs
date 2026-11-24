using System.Text;
using System.Text.Json.Nodes;

namespace Backroom.EndToEndTests;

/// <summary>An access token and the claims the tests check (read without validation: the services validate it).</summary>
internal sealed record AccessToken(string Value)
{
    private JsonNode Claims { get; } = ReadClaims(Value);

    /// <summary>The <c>aud</c> claim as a list, whether the token carries a string or an array.</summary>
    public IReadOnlyList<string> Audiences => Claims["aud"] switch
    {
        JsonArray array => [.. array.Select(audience => audience!.GetValue<string>())],
        JsonValue value => [value.GetValue<string>()],
        _ => [],
    };

    // Never the token itself in test output.
    public override string ToString() => $"access token for [{string.Join(", ", Audiences)}]";

    private static JsonNode ReadClaims(string token)
    {
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            throw new FormatException("Not a JWT.");
        }

        var payload = parts[1].Replace('-', '+').Replace('_', '/');
        payload += new string('=', (4 - (payload.Length % 4)) % 4);
        return JsonNode.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(payload)))
            ?? throw new FormatException("The JWT has no claims.");
    }
}
