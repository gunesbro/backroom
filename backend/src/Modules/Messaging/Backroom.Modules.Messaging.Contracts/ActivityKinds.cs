namespace Backroom.Modules.Messaging.Contracts;

/// <summary>The kinds of feed rows and notifications (docs/contracts/phase-2.md, flow 8, "Inputs").</summary>
public static class ActivityKinds
{
    public const string Mention = "mention";
    public const string GroupMention = "group_mention";
    public const string QuotedReply = "quoted_reply";
    public const string ThreadReply = "thread_reply";
    public const string DirectMessage = "dm_message";
    public const string Added = "added";
    public const string ReminderDue = "reminder_due";
    public const string ScheduledFailed = "scheduled_failed";
    public const string InvitationAccepted = "invitation_accepted";

    /// <summary>Whether the text is a kind this version knows.</summary>
    public static bool IsKnown(string? kind) => kind is Mention or GroupMention or QuotedReply or ThreadReply or DirectMessage
        or Added or ReminderDue or ScheduledFailed or InvitationAccepted;
}
