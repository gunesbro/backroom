using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>A custom status in a live event; <paramref name="ExpiresAt"/> is ISO 8601 (UTC) or null.</summary>
[MessagePackObject]
public sealed record LiveCustomStatus(
    [property: Key("emoji")] string? Emoji,
    [property: Key("text")] string? Text,
    [property: Key("expiresAt")] string? ExpiresAt);
