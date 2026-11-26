using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// A message was deleted (docs/contracts/phase-2.md, "Live events"), sent to the conversation's group. Clients replace the
/// message with its tombstone; the payload carries no content, no author and no client message id.
/// </summary>
/// <param name="Epoch">The cell epoch the numbers belong to.</param>
[MessagePackObject]
public sealed record MessageDeletedPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("conversationId")] Guid ConversationId,
    [property: Key("messageId")] Guid MessageId,
    [property: Key("versionSeq")] long VersionSeq,
    [property: Key("changeSeq")] long ChangeSeq,
    [property: Key("epoch")] string Epoch);
