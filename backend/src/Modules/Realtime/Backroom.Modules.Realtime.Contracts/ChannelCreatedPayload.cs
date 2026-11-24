namespace Backroom.Modules.Realtime.Contracts;

/// <summary>The <c>ChannelCreated</c> payload, sent to the organization's group (no message content).</summary>
public sealed record ChannelCreatedPayload(Guid OrgId, LiveChannel Channel);
