using System.Text.Json.Serialization;

namespace Backroom.Modules.Presence.Presentation;

internal sealed record CustomStatusResponse(
    [property: JsonPropertyName("emoji")] string? Emoji,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt);
