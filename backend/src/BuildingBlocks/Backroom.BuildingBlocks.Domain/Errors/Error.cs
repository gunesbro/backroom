using System.Text.RegularExpressions;

namespace Backroom.BuildingBlocks.Domain.Errors;

/// <summary>
/// An expected failure with a stable, machine-readable code such as <c>messaging.channel_not_found</c>.
/// Clients translate the code. <see cref="Description"/> is short English text that clients may see
/// (except for <see cref="ErrorKind.Unexpected"/>); it must never contain personal data, message
/// content or internal details.
/// </summary>
public sealed partial record Error
{
    private Error(string code, string description, ErrorKind kind, TimeSpan? retryAfter = null)
    {
        if (!CodePattern().IsMatch(code))
        {
            throw new ArgumentException(
                $"Error code '{code}' must look like 'module.snake_case_name'.", nameof(code));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(description);

        Code = code;
        Description = description;
        Kind = kind;
        RetryAfter = retryAfter;
    }

    /// <summary>Stable code: lower-case module prefix and snake_case name, e.g. <c>directory.invitation_invalid</c>.</summary>
    public string Code { get; }

    public string Description { get; }

    public ErrorKind Kind { get; }

    /// <summary>For <see cref="ErrorKind.RateLimited"/>: when the client may try again.</summary>
    public TimeSpan? RetryAfter { get; }

    public static Error Validation(string code, string description) => new(code, description, ErrorKind.Validation);

    public static Error NotFound(string code, string description) => new(code, description, ErrorKind.NotFound);

    public static Error Conflict(string code, string description) => new(code, description, ErrorKind.Conflict);

    public static Error Forbidden(string code, string description) => new(code, description, ErrorKind.Forbidden);

    public static Error ContentTooLarge(string code, string description) => new(code, description, ErrorKind.ContentTooLarge);

    public static Error RateLimited(string code, string description, TimeSpan retryAfter)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(retryAfter, TimeSpan.Zero);
        return new(code, description, ErrorKind.RateLimited, retryAfter);
    }

    public static Error Unexpected(string code, string description) => new(code, description, ErrorKind.Unexpected);

    [GeneratedRegex("^[a-z][a-z0-9_]*(\\.[a-z][a-z0-9_]*)+$", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();
}
