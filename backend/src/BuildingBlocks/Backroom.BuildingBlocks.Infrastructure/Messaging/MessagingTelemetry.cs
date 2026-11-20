using System.Diagnostics;

namespace Backroom.BuildingBlocks.Infrastructure.Messaging;

/// <summary>Spans for publishing and consuming integration events (OpenTelemetry messaging conventions).</summary>
public static class MessagingTelemetry
{
    public const string ActivitySourceName = "Backroom.Messaging";

    /// <summary>AMQP header names carrying the W3C trace context and the organization.</summary>
    public const string TraceParentHeader = "traceparent";

    public const string TraceStateHeader = "tracestate";

    public const string OrganizationHeader = "org_id";

    internal static readonly ActivitySource Source = new(ActivitySourceName);
}
