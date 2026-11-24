namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// Names of the server-to-client methods. Payloads are versioned by name (<c>MessagePosted</c>, later
/// <c>MessagePosted.v2</c>), because clients may talk to cells on different versions.
/// </summary>
public static class LiveEventNames
{
    public const string MessagePosted = "MessagePosted";
    public const string ChannelCreated = "ChannelCreated";
    public const string SessionRevoked = "SessionRevoked";
    public const string PresenceChanged = "PresenceChanged";
    public const string NotificationRequested = "NotificationRequested";
}
