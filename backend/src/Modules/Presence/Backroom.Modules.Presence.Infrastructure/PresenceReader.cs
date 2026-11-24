using Microsoft.Extensions.Options;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.Modules.Presence.Contracts;

namespace Backroom.Modules.Presence.Infrastructure;

/// <summary>
/// <see cref="IPresenceReader"/>: the accounts are first narrowed to active members of the organization of the current unit of work
/// (one statement under row-level security), then read from Valkey in one round trip. Presence lives outside row-level security
/// (owner decision 16), so this check is what keeps people of other organizations out. The time zone is never read.
/// </summary>
internal sealed class PresenceReader(
    IOrganizationMembershipCheck membership, PresenceStore store, IOptions<PresenceOptions> options, TimeProvider timeProvider) : IPresenceReader
{
    public async Task<IReadOnlyList<PresenceEntry>> ReadAsync(IReadOnlyCollection<Guid> accountIds, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accountIds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(accountIds.Count, IPresenceReader.MaxAccounts);
        var distinct = accountIds.Where(id => id != Guid.Empty).Distinct().ToList();
        if (distinct.Count == 0)
        {
            return [];
        }

        var members = await membership.SelectActiveMembersAsync(distinct, cancellationToken);
        var nowMs = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
        var idleAfterMs = (long)options.Value.IdleAfter.TotalMilliseconds;
        var entries = new List<PresenceEntry>(members.Count);
        foreach (var (accountId, connections, status) in await store.ReadAsync(members, cancellationToken))
        {
            entries.Add(new PresenceEntry(
                accountId,
                PresenceRules.StateOf(connections, nowMs, idleAfterMs),
                PresenceRules.DndUntil(status[0], nowMs),
                PresenceRules.Custom(status[1], status[2], status[3], nowMs)));
        }

        return entries;
    }
}
