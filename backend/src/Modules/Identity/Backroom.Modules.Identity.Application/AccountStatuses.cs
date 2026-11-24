using System.Diagnostics;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Modules.Directory.Contracts;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Application;

/// <summary>
/// The account's own manual status (docs/contracts/phase-2.md, flow 9): do not disturb, a custom status and the time zone. In one
/// unit of work the status is replaced (its version grows by one) and an <c>AccountStatusChanged</c> carrying the whole status is queued
/// for every cell where the account has a membership, so the cells hear of it if and only if it was stored, and an older message
/// arriving late changes nothing there. Logs and traces carry the account id only, never the emoji, text or time zone.
/// </summary>
internal sealed partial class AccountStatuses(
    IUnitOfWork unitOfWork,
    IAccountStatusStore store,
    IDirectoryQueries directory,
    ICellLinkOutbox outbox,
    TimeProvider timeProvider,
    ILogger<AccountStatuses> logger)
{
    private static readonly ActivitySource s_activitySource = new(SessionRevocations.ActivitySourceName);

    /// <summary>The account's status as it stands now (expired parts left out); version 0 when none was ever set.</summary>
    public async Task<AccountStatus> GetAsync(Guid accountId, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(accountId, Guid.Empty);
        await unitOfWork.BeginAsync(cancellationToken);
        try
        {
            var status = await store.GetAsync(accountId, cancellationToken);
            return status is null
                ? new AccountStatus(accountId, null, null, null, null, null, 0)
                : AccountStatusRules.Effective(status, timeProvider.GetUtcNow());
        }
        finally
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
        }
    }

    /// <summary>Replaces the status after <see cref="AccountStatusRules.Validate"/>; returns it as it stands now.</summary>
    public async Task<Result<AccountStatus>> SetAsync(
        Guid accountId,
        DateTimeOffset? dndUntil,
        bool hasCustomStatus,
        string? customEmoji,
        string? customText,
        DateTimeOffset? customExpiresAt,
        string? timeZone,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(accountId, Guid.Empty);
        var now = timeProvider.GetUtcNow();
        var validated = AccountStatusRules.Validate(dndUntil, hasCustomStatus, customEmoji, customText, customExpiresAt, timeZone, now);
        if (!validated.IsSuccess)
        {
            return validated.Error;
        }

        using var activity = s_activitySource.StartActivity("account status change");

        // Membership is read before the transaction, on another connection; a membership that appears right now is covered by FullSync.
        var cells = await directory.ListCellsOfAccountAsync(accountId, cancellationToken);
        AccountStatus saved;
        await unitOfWork.BeginAsync(cancellationToken);
        try
        {
            saved = await store.SaveAsync(accountId, validated.GetValueOrThrow(), now, cancellationToken);
            var message = ToMessage(saved);
            foreach (var cellId in cells)
            {
                await outbox.EnqueueAsync(cellId, new ConnectResponse { AccountStatusChanged = message.Clone() }, cancellationToken);
            }

            await unitOfWork.CommitAsync(cancellationToken);
        }
        catch
        {
            await unitOfWork.RollbackAsync(CancellationToken.None);
            throw;
        }

        activity?.SetTag("identity.status.cells", cells.Count);
        LogChanged(logger, accountId, saved.Version, cells.Count);
        return AccountStatusRules.Effective(saved, now);
    }

    /// <summary>The link message for a stored status; empty strings and unset timestamps stand for "none".</summary>
    internal static AccountStatusChanged ToMessage(AccountStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        return new AccountStatusChanged
        {
            AccountId = status.AccountId.ToString("D"),
            DndUntil = status.DndUntil is { } dnd ? Timestamp.FromDateTimeOffset(dnd) : null,
            CustomEmoji = status.CustomEmoji ?? string.Empty,
            CustomText = status.CustomText ?? string.Empty,
            CustomExpiresAt = status.CustomExpiresAt is { } end ? Timestamp.FromDateTimeOffset(end) : null,
            TimeZone = status.TimeZone ?? string.Empty,
            Version = (ulong)status.Version,
        };
    }

    [LoggerMessage(EventId = 10, Level = LogLevel.Information, Message = "The status of account {AccountId} changed to version {Version} and was sent to {Cells} cells.")]
    private static partial void LogChanged(ILogger logger, Guid accountId, long version, int cells);
}
