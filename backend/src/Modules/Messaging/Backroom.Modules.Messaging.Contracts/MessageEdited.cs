using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// A message was edited (docs/contracts/phase-2.md, "Integration events"). Ids and numbers only, never the text.
/// <paramref name="AddedMentionAccountIds"/> are the accounts the new content mentions that the earlier one did not
/// and that can read the conversation: the feed writer notifies them (at most 50).
/// </summary>
public sealed record MessageEdited(
    Guid EventId,
    Guid OrgId,
    DateTimeOffset OccurredAt,
    Guid ConversationId,
    Guid MessageId,
    long VersionSeq,
    IReadOnlyList<Guid> AddedMentionAccountIds) : IIntegrationEvent
{
    public static string EventType => "messaging.message-edited.v1";
}
