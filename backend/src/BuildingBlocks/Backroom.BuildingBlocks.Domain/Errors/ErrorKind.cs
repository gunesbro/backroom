namespace Backroom.BuildingBlocks.Domain.Errors;

/// <summary>
/// The category of an expected failure. Presentation maps it to an HTTP status; it never carries data.
/// Authentication failures (401) are produced by the authentication middleware, not by domain code.
/// </summary>
public enum ErrorKind
{
    /// <summary>The default, so an uninitialized value never looks like a client error.</summary>
    Unexpected = 0,
    Validation,
    NotFound,
    Conflict,
    Forbidden,
    RateLimited,

    /// <summary>The request declares or carries more than is allowed (HTTP 413).</summary>
    ContentTooLarge,
}
