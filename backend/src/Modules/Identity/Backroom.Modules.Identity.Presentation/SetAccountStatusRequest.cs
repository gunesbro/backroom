using System.Text.Json.Serialization;

namespace Backroom.Modules.Identity.Presentation;

/// <summary><c>PUT /api/v1/me/status</c>: the whole status; a missing <c>customStatus</c> clears it, and a missing <c>away</c> is false.</summary>
internal sealed record SetAccountStatusRequest(
    [property: JsonPropertyName("dndUntil")] DateTimeOffset? DndUntil,
    [property: JsonPropertyName("away")] bool Away,
    [property: JsonPropertyName("customStatus")] CustomStatusBody? CustomStatus,
    [property: JsonPropertyName("timeZone")] string? TimeZone);
