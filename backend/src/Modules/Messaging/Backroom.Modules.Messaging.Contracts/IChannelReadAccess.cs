namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// Whether an account may read a channel's messages, for modules that hand out access to them (Realtime's group
/// subscriptions). Phase 1: an open (not archived) public channel of the organization, for an active member; private
/// channels and DMs add their own member lists later. The caller has checked the membership.
/// </summary>
public interface IChannelReadAccess
{
    /// <summary>Runs in the current unit of work, which row-level security limits to the entered organization.</summary>
    Task<bool> CanReadAsync(Guid organizationId, Guid channelId, Guid accountId, CancellationToken cancellationToken);
}
