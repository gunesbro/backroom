using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Permissions.Contracts;

namespace Backroom.Modules.Organization.Application.Invitations;

/// <summary>
/// Pending invitations (docs/contracts/phase-1.md, flow 3, step 8): all of them with
/// <c>members.manage-invitations</c>, otherwise the reader's own. The endpoint has checked the membership.
/// </summary>
internal sealed class InvitationQueries(IInvitationStore invitations, IPermissionChecker permissions)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    private static readonly Error s_pageInvalid = Error.Validation("organization.page_invalid", "The page size must be between 1 and 100.");

    public async Task<Result<InvitationPage>> ListPendingAsync(Guid readerAccountId, Guid? after, int? limit, CancellationToken cancellationToken)
    {
        Guid? createdBy = null;
        if (!await permissions.HasPermissionAsync(readerAccountId, SystemRoles.MembersManageInvitations, cancellationToken))
        {
            if (!await permissions.HasPermissionAsync(readerAccountId, SystemRoles.MembersInviteByEmail, cancellationToken))
            {
                return InvitationCreation.Forbidden;
            }

            createdBy = readerAccountId;
        }

        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            return s_pageInvalid;
        }

        // One more than asked for tells whether another page follows.
        var entries = await invitations.ListPendingAsync(createdBy, after ?? Guid.Empty, pageSize + 1, cancellationToken);
        return entries.Count > pageSize
            ? new InvitationPage([.. entries.Take(pageSize)], entries[pageSize - 1].Id)
            : new InvitationPage(entries, null);
    }
}
