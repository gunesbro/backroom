using System.Collections.Concurrent;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>Stands in for Realtime in a test cell: records what the connector applies, and can be made to fail.</summary>
internal sealed class RecordingSessionRevocation : ISessionRevocation
{
    public ConcurrentQueue<SessionRevocation> Applied { get; } = new();

    public bool Fail { get; set; }

    public Task RevokeAsync(SessionRevocation revocation, CancellationToken cancellationToken)
    {
        if (Fail)
        {
            throw new InvalidOperationException("Valkey is down.");
        }

        Applied.Enqueue(revocation);
        return Task.CompletedTask;
    }
}
