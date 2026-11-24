using Microsoft.Extensions.Logging;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>Keeps every log entry (message, structured values and exception) so a test can search them.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly Lock _lock = new();
    private readonly List<string> _entries = [];

    public IReadOnlyList<string> Entries
    {
        get
        {
            lock (_lock)
            {
                return [.. _entries];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(this, categoryName);

    public void Dispose()
    {
    }

    private void Add(string entry)
    {
        lock (_lock)
        {
            _entries.Add(entry);
        }
    }

    private sealed class CapturingLogger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(' ', pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                : string.Empty;
            provider.Add($"{category}: {formatter(state, exception)} | {values} | {exception}");
        }
    }
}
