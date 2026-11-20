using System.Collections.Concurrent;
using System.Diagnostics;

namespace Backroom.BuildingBlocks.IntegrationTests.Messaging;

/// <summary>Records handler calls and injects failures.</summary>
public sealed class HandlerProbe
{
    private readonly ConcurrentDictionary<Guid, int> _failuresLeft = new();

    public ConcurrentQueue<HandlerCall> Calls { get; } = new();

    public void FailTimes(Guid eventId, int times) => _failuresLeft[eventId] = times;

    public int CallsFor(Guid eventId) => Calls.Count(call => call.EventId == eventId);

    internal void Record(Guid eventId, Guid? organizationId) =>
        Calls.Enqueue(new HandlerCall(eventId, organizationId, Activity.Current?.TraceId));

    /// <summary>True while injected failures remain for <paramref name="eventId"/>; each call uses one.</summary>
    internal bool ShouldFail(Guid eventId)
    {
        while (_failuresLeft.TryGetValue(eventId, out var left) && left > 0)
        {
            if (_failuresLeft.TryUpdate(eventId, left - 1, left))
            {
                return true;
            }
        }

        return false;
    }
}
