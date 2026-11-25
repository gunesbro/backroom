namespace Backroom.Modules.Presence.Contracts;

/// <summary>
/// What a member of the same organization may see of a person: the shown state, do not disturb and the custom status in force
/// (expired ones are left out). Away chosen by hand shows as <see cref="PresenceState.Idle"/>, so it cannot be told from the automatic
/// one. Never the time zone, platform or device.
/// </summary>
public sealed record PresenceEntry(Guid AccountId, PresenceState State, DateTimeOffset? DndUntil, CustomStatus? CustomStatus);
