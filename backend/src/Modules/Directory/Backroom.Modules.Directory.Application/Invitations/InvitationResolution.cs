using System.Security.Cryptography;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// Resolves an invitation code for the invite page (docs/contracts/phase-1.md, flow 3, step 4). Never changes state.
/// Malformed, unknown, revoked, used, failed and expired codes all get <see cref="DirectoryErrors.InvitationInvalid"/>,
/// so the answer does not tell them apart.
/// </summary>
internal sealed class InvitationResolution(
    IUnitOfWork unitOfWork,
    IInvitationRegistry invitations,
    IInvitationEmailHmac emailHmac,
    ICurrentAccount account,
    TimeProvider timeProvider)
{
    public async Task<Result<ResolvedInvitation>> ResolveAsync(string? code, CancellationToken cancellationToken)
    {
        // The same work for a malformed code as for an unknown one would only hide what the format already tells.
        if (InvitationCode.TryHash(code) is not { } codeHash)
        {
            return DirectoryErrors.InvitationInvalid;
        }

        await unitOfWork.BeginAsync(cancellationToken);
        var invitation = await invitations.FindByCodeAsync(codeHash, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        var now = timeProvider.GetUtcNow();
        if (invitation is not { Status: "pending", OrganizationStatus: "active", ExpiresAt: { } expiresAt, EmailHmac: { } invited } || expiresAt <= now)
        {
            return DirectoryErrors.InvitationInvalid;
        }

        // Only a verified email counts; the comparison is of HMACs, so the invited address is never needed.
        var matches = emailHmac.TryCompute(invitation.InvitationId, account.VerifiedEmail) is { } own
            && CryptographicOperations.FixedTimeEquals(own, invited);
        return new ResolvedInvitation(invitation.OrganizationName, expiresAt, matches);
    }
}
