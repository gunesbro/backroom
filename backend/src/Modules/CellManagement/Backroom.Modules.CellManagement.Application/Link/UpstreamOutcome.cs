namespace Backroom.Modules.CellManagement.Application.Link;

/// <summary>The answer to an upstream message: <c>Ack</c> (<see cref="Code"/> null) or <c>Nack</c>.</summary>
internal sealed record UpstreamOutcome(string? Code, bool Retryable)
{
    public static readonly UpstreamOutcome Acknowledged = new(null, false);

    public static UpstreamOutcome Retry(string code) => new(code, true);

    public static UpstreamOutcome Reject(string code) => new(code, false);
}
