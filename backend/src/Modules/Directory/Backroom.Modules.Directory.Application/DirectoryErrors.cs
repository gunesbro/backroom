using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Directory.Application;

internal static class DirectoryErrors
{
    /// <summary>The same answer for unknown organizations and those the caller may not see.</summary>
    public static readonly Error OrganizationNotFound =
        Error.NotFound("directory.organization_not_found", "The organization was not found.");

    public static readonly Error OrganizationInvalid =
        Error.Validation("directory.organization_invalid", "The organization name (1 to 100 characters) or region is not valid.");

    public static readonly Error EmailNotVerified =
        Error.Forbidden("directory.email_not_verified", "Creating an organization needs an account with a verified email address.");

    public static readonly Error EmailInvalid =
        Error.Validation("directory.email_invalid", "The account's email address cannot be used for an organization owner.");

    public static readonly Error NoCellAvailable =
        Error.Conflict("directory.no_cell_available", "No cell in this region can take a new organization right now.");

    public static readonly Error OrganizationCreationBusy =
        Error.RateLimited("directory.organization_creation_busy", "Another organization of this account is being created; try again.", TimeSpan.FromSeconds(1));

    /// <summary>The same answer for malformed, unknown, revoked, used, failed and expired invitation codes.</summary>
    public static readonly Error InvitationInvalid =
        Error.NotFound("directory.invitation_invalid", "This invitation link is not valid. It may have expired or been revoked.");

    /// <summary>The same code as for creating an organization, with a detail that fits an invitation.</summary>
    public static readonly Error InvitationEmailNotVerified =
        Error.Forbidden("directory.email_not_verified", "Joining an organization by invitation needs an account with a verified email address.");

    /// <summary>Only for a usable code; never names the invited address.</summary>
    public static readonly Error InvitationEmailMismatch =
        Error.Forbidden("directory.invitation_email_mismatch", "This invitation was sent to another email address. Sign in with the address it was sent to.");

    public static readonly Error InvitationAcceptanceNotFound =
        Error.NotFound("directory.invitation_acceptance_not_found", "This account has not accepted an invitation.");

    public static Error InvitationRateLimited(TimeSpan retryAfter) =>
        Error.RateLimited("directory.invitation_rate_limited", "Too many invitation attempts. Try again later.", retryAfter);

    public static Error OrganizationLimitReached(TimeSpan retryAfter) =>
        Error.RateLimited("directory.organization_limit", "An account can create at most 5 organizations a day.", retryAfter);
}
