using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Notifications.Application.Settings;

namespace Backroom.Modules.Notifications.Application.Delivery;

/// <summary>
/// The decision order of flow 8 (docs/contracts/phase-2.md): do not disturb, then the conversation setting, then the
/// organization mute, then the account default. A pure function of its inputs.
/// </summary>
/// <remarks>
/// Do not disturb silences what the person's settings would deliver: it turns <see cref="Outcome.Deliver"/> into
/// <see cref="Outcome.BellOnly"/>, so entries collect in the bell. It never adds an entry the settings drop: a conversation
/// set to "nothing" stays out of the bell even while do not disturb is on. Leave and off-hours join the front of the
/// order in Phase 4.
/// </remarks>
internal static class DeliveryRules
{
    public static Outcome Decide(string kind, bool doNotDisturb, ConversationSettings? conversation, AccountSettings account, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(account);
        Outcome byPerson;
        if (conversation is not null)
        {
            byPerson = ByLevel(kind, conversation.Level, conversation.FollowedThreadReplies, conversation.IgnoreGroupMentions);
        }
        else if (account.OrgMutedUntil is { } mutedUntil && mutedUntil > now)
        {
            byPerson = Outcome.BellOnly;
        }
        else
        {
            byPerson = ByLevel(kind, account.DefaultLevel, followedThreadReplies: true, ignoreGroupMentions: false);
        }

        return doNotDisturb && byPerson == Outcome.Deliver ? Outcome.BellOnly : byPerson;
    }

    private static Outcome ByLevel(string kind, NotificationLevel level, bool followedThreadReplies, bool ignoreGroupMentions)
    {
        if (ignoreGroupMentions && string.Equals(kind, ActivityKinds.GroupMention, StringComparison.Ordinal))
        {
            return Outcome.Drop;
        }

        return level switch
        {
            NotificationLevel.All => Outcome.Deliver,
            NotificationLevel.Mentions => IsDirect(kind, followedThreadReplies) ? Outcome.Deliver : Outcome.Drop,
            _ => Outcome.Drop,
        };
    }

    /// <summary>Everything except a plain direct message addresses the person; thread replies count while the person follows them.</summary>
    private static bool IsDirect(string kind, bool followedThreadReplies) =>
        string.Equals(kind, ActivityKinds.ThreadReply, StringComparison.Ordinal)
            ? followedThreadReplies
            : !string.Equals(kind, ActivityKinds.DirectMessage, StringComparison.Ordinal);
}
