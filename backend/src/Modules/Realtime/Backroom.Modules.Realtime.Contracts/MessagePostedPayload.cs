using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>The <c>MessagePosted</c> payload, sent to the channel's group. It is the only live event with content.</summary>
[MessagePackObject]
public sealed record MessagePostedPayload(
    [property: Key("orgId")] Guid OrgId,
    [property: Key("channelId")] Guid ChannelId,
    [property: Key("message")] LiveMessage Message);
