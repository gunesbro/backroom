using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// A message was edited (docs/contracts/phase-2.md, "Live events"), sent to the conversation's group. It carries the new
/// content: <paramref name="Blocks"/> as lists and string-keyed maps of plain values (the shape of
/// <see cref="LiveMessageV2.Blocks"/>) and the plain <paramref name="Text"/>. <paramref name="VersionSeq"/> is the
/// message's new change number and <paramref name="ChangeSeq"/> the conversation's (the same number for an edit).
/// </summary>
/// <param name="EditedAt">The time of the edit, ISO 8601.</param>
/// <param name="Epoch">The cell epoch the numbers belong to.</param>
[MessagePackObject]
public sealed record MessageEditedPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("conversationId")] Guid ConversationId,
    [property: Key("messageId")] Guid MessageId,
    [property: Key("versionSeq")] long VersionSeq,
    [property: Key("changeSeq")] long ChangeSeq,
    [property: Key("blocks")] IReadOnlyList<object> Blocks,
    [property: Key("text")] string Text,
    [property: Key("editedAt")] string EditedAt,
    [property: Key("epoch")] string Epoch);
