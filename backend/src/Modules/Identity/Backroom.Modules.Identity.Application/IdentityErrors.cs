using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Identity.Application;

internal static class IdentityErrors
{
    /// <summary>One code for every rule of the status (flow 9); the description lists the rules, never the value.</summary>
    public static readonly Error StatusInvalid = Error.Validation(
        "identity.status_invalid",
        "The status is not valid: do not disturb ends within 7 days; a custom status has one emoji, up to 100 characters of text, or both, and ends within 30 days; the time zone is an IANA id.");

    public static readonly Error RateLimited =
        Error.RateLimited("identity.rate_limited", "The status was changed too often. Try again in a minute.", TimeSpan.FromMinutes(1));
}
