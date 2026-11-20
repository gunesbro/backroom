using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>
/// Reads an invitation's code hash for the control plane and its link for the email; row-level security limits both to
/// the entered organization.
/// </summary>
internal sealed partial class InvitationLinks(
    IInvitationStore invitations,
    IInvitationLinkBuilder linkBuilder,
    IOrganizationContext context,
    TimeProvider timeProvider,
    ILogger<InvitationLinks> logger) : IInvitationLinks
{
    public async Task<string?> GetCodeHashAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken)
    {
        EnsureInOrganization();
        return await invitations.GetPendingCodeHashAsync(invitationId, codeVersion, timeProvider.GetUtcNow(), cancellationToken) is { } hash
            ? Convert.ToHexStringLower(hash)
            : null;
    }

    public async Task<InvitationLink?> GetLinkAsync(Guid invitationId, int codeVersion, CancellationToken cancellationToken)
    {
        var organizationId = EnsureInOrganization();
        if (await invitations.GetLinkSourceAsync(invitationId, codeVersion, timeProvider.GetUtcNow(), cancellationToken) is not { } source)
        {
            return null;
        }

        try
        {
            if (linkBuilder.Build(invitationId, source) is not { } url)
            {
                // The key version is gone or no longer derives the registered code: the link would not work.
                LogLinkUnavailable(logger, invitationId, codeVersion, source.KeyVersion, organizationId);
                return null;
            }

            return new InvitationLink(url, source.Email, source.OrganizationName);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(source.Salt);
        }
    }

    // Outside an organization row-level security would hide every invitation, and none would ever be registered or sent.
    private Guid EnsureInOrganization() =>
        context.OrganizationId ?? throw new InvalidOperationException("The unit of work has not entered an organization.");

    [LoggerMessage(EventId = 15, Level = LogLevel.Error, Message = "Invitation {InvitationId} code version {CodeVersion} in organization {OrganizationId} gets no link: key version {KeyVersion} is missing or does not derive the registered code.")]
    private static partial void LogLinkUnavailable(ILogger logger, Guid invitationId, int codeVersion, int keyVersion, Guid organizationId);
}
