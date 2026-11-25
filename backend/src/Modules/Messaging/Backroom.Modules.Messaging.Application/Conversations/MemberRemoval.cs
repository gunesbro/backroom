namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>
/// <c>Features:ConversationMemberRemoval</c>: whether a private channel's owner may remove other members. Off by
/// default: an event published in the milliseconds after the removal commits can still reach the removed person's
/// connections, and the owner has accepted that window only for a person's own leave (docs/contracts/phase-2.md,
/// "Live events", "Window after the commit"; owner decision 12). It is turned on once there is a delivery-time
/// membership check or the owner accepts the window for removals by others.
/// </summary>
internal sealed record MemberRemoval(bool Enabled)
{
    public const string Flag = "Features:ConversationMemberRemoval";
}
