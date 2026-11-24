using System.Net;
using System.Text.Json.Nodes;

namespace Backroom.EndToEndTests;

/// <summary>A response's status and JSON body (null when empty).</summary>
internal sealed record ApiResponse(HttpStatusCode Status, JsonNode? Body)
{
    public JsonNode Json => Body ?? throw new InvalidOperationException($"The response ({(int)Status}) has no body.");

    // The status and a problem's error code only: bodies may carry personal data, and the test output must not.
    public override string ToString() =>
        Body is JsonObject problem && problem["code"] is JsonValue code ? $"HTTP {(int)Status} {code}" : $"HTTP {(int)Status}";
}
