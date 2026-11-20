using System.Text.RegularExpressions;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.Directory.Application.Organizations;

/// <summary>
/// Creates an organization (docs/contracts/phase-1.md, flow 2): picks the healthy shared cell of the region
/// with the fewest organizations, stores the organization (<c>provisioning</c>) and the owner's membership
/// (<c>pending</c>), and enqueues <c>OrganizationProvisioned</c> for that cell, all in one unit of work. An
/// account creates at most <see cref="DailyLimit"/> organizations in 24 hours; the count is taken under a
/// per-account lock, so concurrent requests cannot pass it together (a request that finds the lock taken
/// is answered at once). The owner's name, verified email and locale come from the token. Names are
/// normalized and checked by <see cref="DisplayText"/>.
/// </summary>
internal sealed partial class OrganizationCreation(
    IUnitOfWork unitOfWork,
    IOrganizationStore organizations,
    ICellCatalog cells,
    ICellLinkOutbox outbox,
    ICurrentAccount account,
    TimeProvider timeProvider)
{
    public const int DailyLimit = 5;

    public const int MaxNameLength = 100;

    public const int MaxEmailLength = 254;

    /// <summary>The owner's locale when the token has none.</summary>
    public const string DefaultLocale = AccountProfile.DefaultLocale;

    private static readonly TimeSpan s_limitWindow = TimeSpan.FromDays(1);

    public async Task<Result<CreatedOrganization>> CreateAsync(string? name, string? region, CancellationToken cancellationToken)
    {
        if (!DisplayText.TryNormalize(name, MaxNameLength, out var trimmed) || region is null || !RegionPattern().IsMatch(region))
        {
            return DirectoryErrors.OrganizationInvalid;
        }

        var ownerId = account.AccountId;
        if (account.VerifiedEmail is not { } email)
        {
            return DirectoryErrors.EmailNotVerified;
        }

        // The cell stores the owner's email in a 254-character column: refuse it here, before the daily limit counts.
        if (email.Length > MaxEmailLength || email.Any(char.IsControl))
        {
            return DirectoryErrors.EmailInvalid;
        }

        var candidates = await cells.GetHealthyCellsAsync(region, cancellationToken);
        if (candidates.Count == 0)
        {
            return DirectoryErrors.NoCellAvailable;
        }

        await unitOfWork.BeginAsync(cancellationToken);
        var now = timeProvider.GetUtcNow();
        // Another request of this account is creating one right now: answer at once instead of waiting.
        if (await organizations.TryLockAndCountCreatedSinceAsync(ownerId, now - s_limitWindow, cancellationToken) is not var (created, oldest))
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return DirectoryErrors.OrganizationCreationBusy;
        }

        if (created >= DailyLimit)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            var retryAfter = (oldest ?? now) + s_limitWindow - now;
            return DirectoryErrors.OrganizationLimitReached(retryAfter > TimeSpan.Zero ? retryAfter : TimeSpan.FromSeconds(1));
        }

        // The least loaded healthy cell; ties go to the first by id.
        var load = await organizations.CountByCellAsync([.. candidates.Select(cell => cell.Id)], cancellationToken);
        var cell = candidates.MinBy(candidate => load.GetValueOrDefault(candidate.Id))!;

        var organizationId = Guid.CreateVersion7(now);
        await organizations.AddProvisioningAsync(organizationId, trimmed, region, cell.Id, ownerId, now, cancellationToken);
        await outbox.EnqueueAsync(
            cell.Id,
            new ConnectResponse
            {
                OrganizationProvisioned = new OrganizationProvisioned
                {
                    OrganizationId = organizationId.ToString("D"),
                    Name = trimmed,
                    OwnerAccountId = ownerId.ToString("D"),
                    // The display name is editable in Keycloak: the same rules as the organization name.
                    OwnerDisplayName = AccountProfile.DisplayNameOrEmail(account.DisplayName, email),
                    OwnerEmail = email,
                    OwnerLocale = AccountProfile.LocaleOrDefault(account.Locale),
                },
            },
            cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return new CreatedOrganization(organizationId, "provisioning", cell.Id, cell.PublicUrl);
    }

    [GeneratedRegex(@"\A[a-z]{2}(-[a-z0-9]+){0,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();
}
