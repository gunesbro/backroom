using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>The <c>ChannelCreated</c> payload, sent to the organization's group (no message content).</summary>
[MessagePackObject]
public sealed record ChannelCreatedPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("channel")] LiveChannel Channel);
