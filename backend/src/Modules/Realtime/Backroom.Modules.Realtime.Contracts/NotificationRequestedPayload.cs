using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// The <c>NotificationRequested</c> payload (docs/contracts/phase-2.md, "Live events"), sent only to the recipient's account group
/// in one organization. <c>preview</c> is at most <see cref="MaxPreviewLength"/> characters of the message, read through the
/// recipient's permission check, or null (none, or the recipient hides previews). Never logged.
/// </summary>
[MessagePackObject]
public sealed record NotificationRequestedPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("accountId")] Guid AccountId,
    [property: Key("notificationId")] Guid NotificationId,
    [property: Key("kind")] string Kind,
    [property: Key("conversationId")] Guid? ConversationId,
    [property: Key("messageId")] Guid? MessageId,
    [property: Key("actorAccountId")] Guid ActorAccountId,
    [property: Key("preview")] string? Preview)
{
    /// <summary>The longest preview a live notification carries (decision 13).</summary>
    public const int MaxPreviewLength = 150;
}
