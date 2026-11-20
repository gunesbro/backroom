namespace Backroom.BuildingBlocks.Domain.Events;

/// <summary>
/// An event one module publishes for others (docs/adr/0006). Events are immutable records in the
/// publishing module's Contracts project. They carry ids, never message text, email content or
/// other personal data beyond what the contract lists.
/// </summary>
/// <remarks>
/// Serialized as JSON with snake_case names, so <see cref="OrgId"/> becomes <c>org_id</c>.
/// Adding an optional property is compatible; renaming or removing one needs a new version.
/// </remarks>
public interface IIntegrationEvent
{
    /// <summary>
    /// Stable name <c>{module}.{event-name}.v{version}</c>, e.g. <c>organization.member-added.v1</c>.
    /// The module part is the publishing module's schema and selects its <c>{module}.events</c> exchange.
    /// </summary>
    static abstract string EventType { get; }

    /// <summary>UUIDv7; consumers use it to process each event once.</summary>
    Guid EventId { get; }

    /// <summary>The organization the event belongs to; consumers run in its context.</summary>
    Guid OrgId { get; }

    DateTimeOffset OccurredAt { get; }
}
