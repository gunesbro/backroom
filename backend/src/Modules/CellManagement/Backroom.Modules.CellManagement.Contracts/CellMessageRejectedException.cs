namespace Backroom.Modules.CellManagement.Contracts;

/// <summary>
/// Thrown by an <see cref="ICellMessageHandler{T}"/> to refuse a message for good: the cell gets a
/// non-retryable <c>Nack</c> with <see cref="Code"/> (lower-case letters, digits, '_' and '.') and parks it.
/// </summary>
public sealed class CellMessageRejectedException : Exception
{
    public CellMessageRejectedException()
        : this("rejected")
    {
    }

    public CellMessageRejectedException(string code)
        : base($"The message was rejected: {code}.") => Code = code;

    public CellMessageRejectedException(string code, Exception innerException)
        : base($"The message was rejected: {code}.", innerException) => Code = code;

    public string Code { get; }
}
