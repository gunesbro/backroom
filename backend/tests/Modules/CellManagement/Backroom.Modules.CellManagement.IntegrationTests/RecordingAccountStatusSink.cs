using System.Collections.Concurrent;
using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>Stands in for Presence in a test cell: records what the connector applies (newer versions only, like Valkey), and can fail.</summary>
internal sealed class RecordingAccountStatusSink : IAccountStatusSink
{
    private readonly ConcurrentDictionary<Guid, long> _versions = new();

    public ConcurrentQueue<AccountStatusCopy> Applied { get; } = new();

    public bool Fail { get; set; }

    public Task<bool> ApplyAsync(AccountStatusCopy status, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(status);
        if (Fail)
        {
            throw new InvalidOperationException("Valkey is down.");
        }

        var newer = false;
        _versions.AddOrUpdate(
            status.AccountId,
            _ =>
            {
                newer = true;
                return status.Version;
            },
            (_, held) =>
            {
                newer = status.Version > held;
                return newer ? status.Version : held;
            });
        if (newer)
        {
            Applied.Enqueue(status);
        }

        return Task.FromResult(newer);
    }
}
