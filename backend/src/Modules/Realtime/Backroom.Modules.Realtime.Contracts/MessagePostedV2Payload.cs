using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// The <c>MessagePosted.v2</c> payload (docs/contracts/phase-2.md, "Live events"), sent to the conversation's group.
/// <paramref name="ChangeSeq"/> is the conversation's change counter after this message, and <paramref name="Epoch"/>
/// the cell epoch every number in the payload belongs to: a client holding another epoch runs a full resync.
/// </summary>
[MessagePackObject]
public sealed record MessagePostedV2Payload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("conversationId")] Guid ConversationId,
    [property: Key("message")] LiveMessageV2 Message,
    [property: Key("changeSeq")] long ChangeSeq,
    [property: Key("epoch")] string Epoch);
