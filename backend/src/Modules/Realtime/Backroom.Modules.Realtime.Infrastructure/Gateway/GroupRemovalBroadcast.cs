using Microsoft.Extensions.Options;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// Tells every app node (through <see cref="GroupRemovalChannel"/>) to take the account out of the organization's
/// groups. It throws when Valkey does not accept the message in time, so the event consumer fails and RabbitMQ delivers
/// the event again: a removal is never silently lost at this step.
/// </summary>
internal sealed class GroupRemovalBroadcast(ValkeyConnection valkey, IOptions<RealtimeOptions> options, TimeProvider timeProvider)
{
    public async Task PublishAsync(Guid organizationId, Guid accountId, CancellationToken cancellationToken)
    {
        var connection = await valkey.GetAsync(cancellationToken);
        var send = connection.GetSubscriber().PublishAsync(GroupRemovalChannel.Name, GroupRemovalChannel.Encode(organizationId, accountId));
        await send.WaitAsync(options.Value.PublishTimeout, timeProvider, cancellationToken);
    }
}
