using System.Security.Cryptography;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Accounts;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Domain.Email;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// Accepts an invitation for the signed-in account (docs/contracts/phase-1.md, flow 3, steps 5 and 7, and "Invitation
/// security"). In one unit of work and under the invitation's lock: only a pending, unexpired invitation of an active
/// organization, only for an account whose verified email has the invited email's HMAC; then <c>pending</c> →
/// <c>accepting</c>, a pending membership, and <c>InvitationAccepted</c> to the organization's cell. A repeat by the
/// same account answers the current state; every other unusable code gets the same
/// <see cref="DirectoryErrors.InvitationInvalid"/> as resolving it.
/// </summary>
internal sealed partial class InvitationAcceptance(
    IUnitOfWork unitOfWork,
    IInvitationRegistry invitations,
    IInvitationEmailHmac emailHmac,
    ICellLinkOutbox outbox,
    ICurrentAccount account,
    TimeProvider timeProvider,
    ILogger<InvitationAcceptance> logger)
{
    public async Task<Result<InvitationAcceptanceState>> AcceptAsync(string? code, CancellationToken cancellationToken)
    {
        if (InvitationCode.TryHash(code) is not { } codeHash)
        {
            return DirectoryErrors.InvitationInvalid;
        }

        var accountId = account.AccountId;
        await unitOfWork.BeginAsync(cancellationToken);
        var invitation = await invitations.LockByCodeAsync(codeHash, cancellationToken);

        // The same account pressing "Join" again, or another tab: the state so far, nothing changes.
        if (invitation is { Status: "accepting" or "used" } && invitation.AcceptingAccountId == accountId)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return new InvitationAcceptanceState(invitation.OrganizationId, invitation.Status, null);
        }

        // Postgres keeps microseconds: the stored and the sent time are the same instant.
        var now = timeProvider.GetUtcNow();
        now = now.AddTicks(-(now.Ticks % 10));
        if (invitation is not { Status: "pending", OrganizationStatus: "active", ExpiresAt: { } expiresAt, EmailHmac: { } invited }
            || expiresAt <= now)
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            return DirectoryErrors.InvitationInvalid;
        }

        // Checked only for a usable code, so these answers say nothing about codes that are not.
        if (account.VerifiedEmail is not { } verifiedEmail || !EmailAddress.TryNormalize(verifiedEmail, out var email))
        {
            await unitOfWork.RollbackAsync(cancellationToken);
            LogRefused(logger, invitation.InvitationId, accountId, "email_not_verified");
            return DirectoryErrors.InvitationEmailNotVerified;
        }

        if (emailHmac.TryCompute(invitation.InvitationId, email) is not { } own || !CryptographicOperations.FixedTimeEquals(own, invited))
        {
            // The invitation stays pending: the invited person can still accept it.
            await unitOfWork.RollbackAsync(cancellationToken);
            LogRefused(logger, invitation.InvitationId, accountId, "email_mismatch");
            return DirectoryErrors.InvitationEmailMismatch;
        }

        if (!await invitations.StartAcceptanceAsync(invitation.InvitationId, invitation.OrganizationId, accountId, now, cancellationToken))
        {
            // Not reachable under the lock; refused like any invitation that is no longer pending, and logged
            // because it would mean another writer changed the invitation without its lock.
            await unitOfWork.RollbackAsync(cancellationToken);
            LogNotPendingUnderLock(logger, invitation.InvitationId);
            return DirectoryErrors.InvitationInvalid;
        }

        await outbox.EnqueueAsync(
            invitation.CellId,
            new ConnectResponse
            {
                InvitationAccepted = new InvitationAccepted
                {
                    OrganizationId = invitation.OrganizationId.ToString("D"),
                    InvitationId = invitation.InvitationId.ToString("D"),
                    AccountId = accountId.ToString("D"),
                    DisplayName = AccountProfile.DisplayNameOrEmail(account.DisplayName, email),
                    Email = email,
                    Locale = AccountProfile.LocaleOrDefault(account.Locale),
                    AcceptedAt = Timestamp.FromDateTimeOffset(now),
                },
            },
            cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        LogAccepting(logger, invitation.InvitationId, invitation.OrganizationId, accountId, invitation.CellId);
        return new InvitationAcceptanceState(invitation.OrganizationId, "accepting", null);
    }

    /// <summary>The signed-in account's most recent acceptance, which the invite page polls.</summary>
    public async Task<Result<InvitationAcceptanceState>> GetLatestAsync(CancellationToken cancellationToken)
    {
        await unitOfWork.BeginAsync(cancellationToken);
        var latest = await invitations.FindLatestAcceptanceAsync(account.AccountId, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);
        return latest is null ? DirectoryErrors.InvitationAcceptanceNotFound : latest;
    }

    [LoggerMessage(EventId = 25, Level = LogLevel.Information, Message = "Security: invitation {InvitationId} of organization {OrganizationId} is being accepted by account {AccountId}; sent to cell {CellId}.")]
    private static partial void LogAccepting(ILogger logger, Guid invitationId, Guid organizationId, Guid accountId, string cellId);

    [LoggerMessage(EventId = 26, Level = LogLevel.Warning, Message = "Security: accepting invitation {InvitationId} was refused for account {AccountId} ({Reason}).")]
    private static partial void LogRefused(ILogger logger, Guid invitationId, Guid accountId, string reason);

    [LoggerMessage(EventId = 29, Level = LogLevel.Error, Message = "Invitation {InvitationId} was pending under its lock, but its acceptance did not apply.")]
    private static partial void LogNotPendingUnderLock(ILogger logger, Guid invitationId);
}
