namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// Names of the server-to-client methods. Payloads are versioned by name (<c>MessagePosted</c>, later
/// <c>MessagePosted.v2</c>), because clients may talk to cells on different versions.
/// </summary>
public static class LiveEventNames
{
    public const string MessagePosted = "MessagePosted";

    /// <summary>Replaces <see cref="MessagePosted"/>: adds the version and change numbers and the cell epoch.</summary>
    public const string MessagePostedV2 = "MessagePosted.v2";
    public const string ChannelCreated = "ChannelCreated";
    public const string SessionRevoked = "SessionRevoked";
    public const string PresenceChanged = "PresenceChanged";
    public const string NotificationRequested = "NotificationRequested";
    public const string ConversationUpdated = "ConversationUpdated";
    public const string MembershipChanged = "MembershipChanged";
    public const string FileRejected = "FileRejected";
}
