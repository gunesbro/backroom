namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// Thrown by an <see cref="IDownstreamHandler{T}"/> when the cell has not seen what the message builds on yet (an
/// invitation of an organization it does not know): a retryable <c>Nack</c> with <see cref="Code"/> (lower-case
/// letters, digits, '_' and '.'), because a message waiting for a retry can be overtaken by later ones
/// (docs/contracts/phase-1.md, "Delivery").
/// </summary>
internal sealed class LinkPrerequisiteMissingException : Exception
{
    public LinkPrerequisiteMissingException()
        : this("link.prerequisite_missing")
    {
    }

    public LinkPrerequisiteMissingException(string code)
        : base($"The message waits for a prerequisite: {code}.") => Code = code;

    public LinkPrerequisiteMissingException(string code, Exception innerException)
        : base($"The message waits for a prerequisite: {code}.", innerException) => Code = code;

    public string Code { get; }
}
