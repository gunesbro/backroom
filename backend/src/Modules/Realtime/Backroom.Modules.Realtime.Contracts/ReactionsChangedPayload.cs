using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// A message's reactions changed (docs/contracts/phase-2.md, "Live events"), sent to the conversation's group: the whole new
/// summary of the message (<paramref name="Reactions"/>, empty when the last reaction went), the message's new change number
/// (<paramref name="VersionSeq"/>) and the conversation's (<paramref name="ChangeSeq"/>, the same number for a reaction). No
/// <c>reactedByMe</c>: the client fills it from its own state.
/// </summary>
/// <param name="Epoch">The cell epoch the numbers belong to.</param>
[MessagePackObject]
public sealed record ReactionsChangedPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("conversationId")] Guid ConversationId,
    [property: Key("messageId")] Guid MessageId,
    [property: Key("versionSeq")] long VersionSeq,
    [property: Key("changeSeq")] long ChangeSeq,
    [property: Key("reactions")] IReadOnlyList<LiveReaction> Reactions,
    [property: Key("epoch")] string Epoch);
