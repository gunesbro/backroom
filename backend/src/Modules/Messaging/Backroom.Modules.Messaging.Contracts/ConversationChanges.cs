namespace Backroom.Modules.Messaging.Contracts;

/// <summary>The values of <see cref="ConversationChanged.Change"/>.</summary>
public static class ConversationChanges
{
    public const string Renamed = "renamed";
    public const string Archived = "archived";
    public const string Unarchived = "unarchived";
    public const string OwnerTransferred = "owner_transferred";

    /// <summary>A group DM became a private channel (Phase 2 step 8).</summary>
    public const string Converted = "converted";
}
