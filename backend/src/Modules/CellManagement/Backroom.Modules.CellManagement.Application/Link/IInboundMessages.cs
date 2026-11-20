namespace Backroom.Modules.CellManagement.Application.Link;

/// <summary>Ids of handled upstream messages (<c>cell_management.inbound_messages</c>), in the scope's unit of work.</summary>
internal interface IInboundMessages
{
    /// <summary>Records the message; <see langword="false"/> if it was recorded before (a duplicate).</summary>
    Task<bool> TryRecordAsync(Guid messageId, string cellId, string type, DateTimeOffset now, CancellationToken cancellationToken);
}
