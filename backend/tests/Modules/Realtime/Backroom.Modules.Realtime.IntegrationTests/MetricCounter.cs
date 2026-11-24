using System.Diagnostics.Metrics;
using Backroom.Modules.Realtime.Infrastructure;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>Collects what the gateway's meter records from its creation on.</summary>
internal sealed class MetricCounter : IDisposable
{
    private readonly Lock _lock = new();
    private readonly MeterListener _listener = new();
    private readonly Dictionary<string, long> _totals = [];
    private readonly Dictionary<string, long> _increases = [];

    public MetricCounter()
    {
        _listener.InstrumentPublished = (instrument, listener) =>
        {
            if (instrument.Meter.Name == RealtimeTelemetry.MeterName)
            {
                listener.EnableMeasurementEvents(instrument);
            }
        };
        _listener.SetMeasurementEventCallback<long>((instrument, value, _, _) =>
        {
            lock (_lock)
            {
                _totals[instrument.Name] = _totals.GetValueOrDefault(instrument.Name) + value;
                if (value > 0)
                {
                    _increases[instrument.Name] = _increases.GetValueOrDefault(instrument.Name) + value;
                }
            }
        });
        _listener.Start();
    }

    /// <summary>The sum of the measurements, decreases included.</summary>
    public long Total(string name)
    {
        lock (_lock)
        {
            return _totals.GetValueOrDefault(name);
        }
    }

    /// <summary>The sum of the positive measurements only (a connection closing after the listener started does not hide an opening).</summary>
    public long Increases(string name)
    {
        lock (_lock)
        {
            return _increases.GetValueOrDefault(name);
        }
    }

    public void Dispose() => _listener.Dispose();
}
