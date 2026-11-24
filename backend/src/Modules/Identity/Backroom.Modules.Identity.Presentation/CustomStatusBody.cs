using System.Text.Json.Serialization;

namespace Backroom.Modules.Identity.Presentation;

/// <summary>The custom status as clients send and read it; every part may be absent.</summary>
internal sealed record CustomStatusBody(
    [property: JsonPropertyName("emoji")] string? Emoji,
    [property: JsonPropertyName("text")] string? Text,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset? ExpiresAt);
