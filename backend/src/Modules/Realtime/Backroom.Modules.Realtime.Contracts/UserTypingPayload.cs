using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// The <c>UserTyping</c> payload (docs/contracts/phase-2.md, "Live events"), sent to the conversation's group except the
/// sender's own connection when a member who may post calls the hub's <c>Typing</c>. Never stored. <c>threadId</c> is
/// reserved for threads (step 12); until then it is always null.
/// </summary>
[MessagePackObject]
public sealed record UserTypingPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("conversationId")] Guid ConversationId,
    [property: Key("threadId")] Guid? ThreadId,
    [property: Key("accountId")] Guid AccountId);
