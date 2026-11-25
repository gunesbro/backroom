namespace Backroom.Modules.Notifications.Application.Settings;

/// <summary>What a person wants to be told about (docs/contracts/phase-2.md, flow 8, "Decision order").</summary>
internal enum NotificationLevel
{
    /// <summary>Every message and reply.</summary>
    All,

    /// <summary>Mentions and replies to the person; plain messages stay quiet.</summary>
    Mentions,

    /// <summary>Nothing (the unread badge stays; that is Messaging's).</summary>
    None,
}
