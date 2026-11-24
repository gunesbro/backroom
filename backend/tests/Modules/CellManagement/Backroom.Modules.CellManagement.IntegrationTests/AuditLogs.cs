using Microsoft.Extensions.Logging;

namespace Backroom.Modules.CellManagement.IntegrationTests;

/// <summary>Collects every formatted log message, to check what the ops audit events contain.</summary>
internal sealed class AuditLogs : ILoggerProvider
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new Collector(this);

    public void Dispose()
    {
    }

    private sealed class Collector(AuditLogs owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner._messages)
            {
                owner._messages.Add(formatter(state, exception));
            }
        }
    }
}
