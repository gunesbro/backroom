using System.Text.Json.Serialization;

namespace Backroom.Modules.Presence.Presentation;

/// <summary>State and status only: never the time zone, platform or device.</summary>
internal sealed record PersonPresenceResponse(
    [property: JsonPropertyName("accountId")] Guid AccountId,
    [property: JsonPropertyName("state")] string State,
    [property: JsonPropertyName("dndUntil")] DateTimeOffset? DndUntil,
    [property: JsonPropertyName("customStatus")] CustomStatusResponse? CustomStatus);
