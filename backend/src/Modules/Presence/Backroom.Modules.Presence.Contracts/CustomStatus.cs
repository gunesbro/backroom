namespace Backroom.Modules.Presence.Contracts;

/// <summary>A custom status in force: an emoji, a text or both, and an optional end.</summary>
public sealed record CustomStatus(string? Emoji, string? Text, DateTimeOffset? ExpiresAt);
