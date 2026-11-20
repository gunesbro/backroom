namespace Backroom.Modules.CellManagement.Contracts;

/// <summary>A durable link message, its id and the cell it was sent to.</summary>
public sealed record CellMessage<T>(Guid MessageId, string CellId, T Payload);
