namespace Backroom.Modules.CellManagement.Presentation;

/// <summary>A cell in the operations panel: <c>status</c> is <c>pending</c> or <c>registered</c>, <c>linkStatus</c> is <c>connected</c> or <c>disconnected</c>.</summary>
public sealed record CellSummaryResponse(
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
