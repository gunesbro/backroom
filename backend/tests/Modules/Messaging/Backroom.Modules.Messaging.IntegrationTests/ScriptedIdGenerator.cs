using System.Collections.Concurrent;
using Backroom.BuildingBlocks.Application.Identifiers;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>Hands out the queued ids first (to make ids and commit order disagree), then real ones.</summary>
internal sealed class ScriptedIdGenerator : IIdGenerator
{
    private readonly UuidV7IdGenerator _inner = new(TimeProvider.System);
    private readonly ConcurrentQueue<Guid> _queue = new();

    public void Enqueue(Guid id) => _queue.Enqueue(id);

    public Guid NewId() => _queue.TryDequeue(out var id) ? id : _inner.NewId();
}
