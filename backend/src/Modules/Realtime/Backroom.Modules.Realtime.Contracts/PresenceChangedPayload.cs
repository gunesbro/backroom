using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// The <c>PresenceChanged</c> payload (docs/contracts/phase-2.md, "Live events"), sent to the person's presence group in one
/// organization: <c>state</c> is <c>online</c>, <c>idle</c> or <c>offline</c>; <c>dndUntil</c> is ISO 8601 (UTC) or null. Never the
/// time zone, platform or device. Clients treat a do not disturb or custom status whose end has passed as unset.
/// </summary>
[MessagePackObject]
public sealed record PresenceChangedPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("accountId")] Guid AccountId,
    [property: Key("state")] string State,
    [property: Key("dndUntil")] string? DndUntil,
    [property: Key("customStatus")] LiveCustomStatus? CustomStatus);
