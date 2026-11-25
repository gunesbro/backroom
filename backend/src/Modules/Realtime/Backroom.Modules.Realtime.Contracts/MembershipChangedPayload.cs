using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// The account was added to or removed from a conversation (docs/contracts/phase-2.md, "Live events"). Sent to that
/// account's own group only; it carries ids and never a name.
/// </summary>
/// <param name="AccountId">The account whose membership changed; also the group the event goes to.</param>
/// <param name="Change"><see cref="Added"/> or <see cref="Removed"/>.</param>
[MessagePackObject]
public sealed record MembershipChangedPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("accountId")] Guid AccountId,
    [property: Key("conversationId")] Guid ConversationId,
    [property: Key("change")] string Change)
{
    public const string Added = "added";
    public const string Removed = "removed";
}
