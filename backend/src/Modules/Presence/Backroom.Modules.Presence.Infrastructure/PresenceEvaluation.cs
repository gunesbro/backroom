using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>What an evaluation found to publish: the state, the organizations where the person is watched, and the status in force.</summary>
internal sealed record PresenceEvaluation(
    Guid AccountId, PresenceState State, IReadOnlyList<Guid> Organizations, DateTimeOffset? DndUntil, CustomStatus? CustomStatus);
