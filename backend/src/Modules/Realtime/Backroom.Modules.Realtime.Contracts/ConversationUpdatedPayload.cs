using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// A conversation's name, description or archive state changed (docs/contracts/phase-2.md, "Live events"). Sent to the
/// conversation's group only, so a private channel's name reaches nobody but its subscribed members.
/// </summary>
/// <param name="ArchivedAt">ISO 8601, or <see langword="null"/> while the conversation is open.</param>
/// <param name="ChangeSeq">The conversation's change number of this change.</param>
/// <param name="Epoch">The cell epoch the change number belongs to.</param>
[MessagePackObject]
public sealed record ConversationUpdatedPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("conversationId")] Guid ConversationId,
    [property: Key("name")] string? Name,
    [property: Key("description")] string? Description,
    [property: Key("archivedAt")] string? ArchivedAt,
    [property: Key("memberCount")] int MemberCount,
    [property: Key("changeSeq")] long ChangeSeq,
    [property: Key("epoch")] string Epoch);
