using System.Diagnostics;

namespace Backroom.BuildingBlocks.IntegrationTests.Messaging;

public sealed record HandlerCall(Guid EventId, Guid? OrganizationId, ActivityTraceId? TraceId);
