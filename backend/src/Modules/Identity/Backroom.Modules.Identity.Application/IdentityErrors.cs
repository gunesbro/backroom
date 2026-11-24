using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Identity.Application;

internal static class IdentityErrors
{
    /// <summary>One code for every rule of the status (flow 9); the description lists the rules, never the value.</summary>
    public static readonly Error StatusInvalid = Error.Validation(
        "identity.status_invalid",
        "The status is not valid: do not disturb ends within 7 days; a custom status has one emoji, up to 100 characters of text, or both, and ends within 30 days; the time zone is an IANA id (identity.time_zone_invalid when only that is wrong).");

    /// <summary>Its own code, so a client whose zone the server does not know can fall back to UTC.</summary>
    public static readonly Error TimeZoneInvalid =
        Error.Validation("identity.time_zone_invalid", "The time zone is not an IANA time zone id this server knows.");

    public static readonly Error RateLimited =
        Error.RateLimited("identity.rate_limited", "The status was changed too often. Try again in a minute.", TimeSpan.FromMinutes(1));
}
