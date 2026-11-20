namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>Thrown by an <see cref="IDownstreamHandler{T}"/> to refuse a message for good with <see cref="Code"/> (lower-case letters, digits, '_' and '.').</summary>
internal sealed class LinkMessageRejectedException : Exception
{
    public LinkMessageRejectedException()
        : this("rejected")
    {
    }

    public LinkMessageRejectedException(string code)
        : base($"The message was rejected: {code}.") => Code = code;

    public LinkMessageRejectedException(string code, Exception innerException)
        : base($"The message was rejected: {code}.", innerException) => Code = code;

    public string Code { get; }
}
