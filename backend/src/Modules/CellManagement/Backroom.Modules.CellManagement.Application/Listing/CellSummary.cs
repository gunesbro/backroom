namespace Backroom.Modules.CellManagement.Application.Listing;

/// <summary>A cell for the operations panel. Link status is <c>connected</c> or <c>disconnected</c>.</summary>
internal sealed record CellSummary(
    string Id,
    string Type,
    string Region,
    string Status,
    string? Version,
    string LinkStatus,
    DateTimeOffset? LastHeartbeatAt,
    int OrganizationCount,
    int PendingMessages,
    int RejectedMessages);
