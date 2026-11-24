using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>The <c>SessionRevoked</c> payload, sent to one connection just before the server closes it.</summary>
[MessagePackObject]
public sealed record SessionRevokedPayload([property: Key("reason")] string Reason)
{
    /// <summary>The reason for every revocation in Phase 1: the person signed out or the session was ended.</summary>
    public const string SessionEnded = "session_ended";
}
