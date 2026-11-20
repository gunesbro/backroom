using System.Text.RegularExpressions;
using Backroom.BuildingBlocks.Domain.Events;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>Rules for integration event type names: <c>{module}.{event-name}.v{version}</c>.</summary>
public static partial class IntegrationEventTypes
{
    /// <summary>Throws unless <paramref name="eventType"/> is a valid event type name; returns it.</summary>
    public static string Validate(string eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        if (!IsValid(eventType))
        {
            throw new ArgumentException(
                $"Event type '{eventType}' must look like 'module.event-name.v1' (module in snake_case).", nameof(eventType));
        }

        return eventType;
    }

    /// <summary>Longest event type; queue names (consumer schema, type, suffixes) must stay under 255 bytes.</summary>
    public const int MaxLength = 120;

    public static bool IsValid(string? eventType) =>
        eventType is { Length: <= MaxLength } && EventTypePattern().IsMatch(eventType);

    /// <summary>The publishing module, which is also its schema name.</summary>
    public static string ModuleOf(string eventType) => Validate(eventType)[..eventType.IndexOf('.', StringComparison.Ordinal)];

    /// <summary>The topic exchange a module publishes to.</summary>
    public static string ExchangeFor(string module) => module + ".events";

    // Module: a snake_case schema name. Event name: kebab-case. Version: v1 to v9999.
    [GeneratedRegex(@"^[a-z][a-z0-9_]{0,62}\.[a-z][a-z0-9]*(-[a-z0-9]+)*\.v[1-9][0-9]{0,3}$", RegexOptions.CultureInvariant)]
    private static partial Regex EventTypePattern();
}

/// <summary>Validated names for one event type, computed once per type.</summary>
internal static class IntegrationEventMetadata<TEvent>
    where TEvent : IIntegrationEvent
{
    public static readonly string EventType = IntegrationEventTypes.Validate(TEvent.EventType);

    public static readonly string Module = IntegrationEventTypes.ModuleOf(EventType);
}
