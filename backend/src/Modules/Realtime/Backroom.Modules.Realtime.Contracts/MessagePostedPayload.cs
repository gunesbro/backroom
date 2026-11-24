namespace Backroom.Modules.Realtime.Contracts;

/// <summary>The <c>MessagePosted</c> payload, sent to the channel's group. It is the only live event with content.</summary>
public sealed record MessagePostedPayload(Guid OrgId, Guid ChannelId, LiveMessage Message);
