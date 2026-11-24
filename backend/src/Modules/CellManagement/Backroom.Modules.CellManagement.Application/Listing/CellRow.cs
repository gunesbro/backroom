namespace Backroom.Modules.CellManagement.Application.Listing;

/// <summary>A cell as stored, with its pending and rejected downstream message counts.</summary>
internal sealed record CellRow(
    string Id,
    string Type,
    string Region,
    string Status,
    string? Version,
    DateTimeOffset? LastHeartbeatAt,
    int PendingMessages,
    int RejectedMessages);
