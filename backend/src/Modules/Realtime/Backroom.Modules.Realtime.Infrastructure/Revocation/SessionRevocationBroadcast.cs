using Microsoft.Extensions.Options;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Modules.Realtime.Infrastructure.Gateway;

namespace Backroom.Modules.Realtime.Infrastructure.Revocation;

/// <summary>
/// Tells every app node (through <see cref="SessionRevocationChannel"/>) about a revocation. It throws when Valkey does not
/// accept the message in time, so the connector answers the link message with a retryable <c>Nack</c> and the control plane
/// sends it again: an announcement is never silently lost at this step.
/// </summary>
internal sealed class SessionRevocationBroadcast(ValkeyConnection valkey, IOptions<RealtimeOptions> options, TimeProvider timeProvider)
{
    public async Task PublishAsync(SessionRevocation revocation, CancellationToken cancellationToken)
    {
        var connection = await valkey.GetAsync(cancellationToken);
        var send = connection.GetSubscriber().PublishAsync(SessionRevocationChannel.Name, SessionRevocationChannel.Encode(revocation));
        await send.WaitAsync(options.Value.CommandTimeout, timeProvider, cancellationToken);
    }
}
