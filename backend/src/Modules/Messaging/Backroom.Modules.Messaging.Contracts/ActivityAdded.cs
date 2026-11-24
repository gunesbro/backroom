using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// Feed rows were added for people (docs/contracts/phase-2.md, flow 8 and "Integration events"). Notifications decides
/// what each person gets. Ids only, never text: Notifications reads the message through Messaging's permission check.
/// <paramref name="ConversationId"/> and <paramref name="MessageId"/> are <see langword="null"/> for kinds that point at
/// neither (<see cref="ActivityKinds.InvitationAccepted"/>). At most <see cref="MaxAccounts"/> accounts per event.
/// </summary>
public sealed record ActivityAdded(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    string Kind,
    Guid? ConversationId,
    Guid? MessageId,
    Guid ActorAccountId,
    IReadOnlyList<Guid> AccountIds) : IIntegrationEvent
{
    /// <summary>The most accounts one event names (the contract's limit).</summary>
    public const int MaxAccounts = 500;

    public static string EventType => "messaging.activity-added.v1";
}
