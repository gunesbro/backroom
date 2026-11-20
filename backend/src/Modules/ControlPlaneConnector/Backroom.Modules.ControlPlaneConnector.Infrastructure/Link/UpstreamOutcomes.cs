using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// What the cell does once the control plane acknowledged an upstream message, before the message is settled
/// (docs/contracts/phase-1.md, flow 3, step 3): an acknowledged <c>InvitationRegistered</c> marks that code version
/// registered through <see cref="IInvitationRegistration"/>, which lets the email go out. The outcome runs first and
/// the settlement after it, so a crash in between only repeats the outcome, which is idempotent.
/// </summary>
internal sealed partial class UpstreamOutcomes(IServiceScopeFactory scopes, ILogger<UpstreamOutcomes> logger)
{
    /// <summary>The outcome to apply when <paramref name="message"/> is acknowledged, if it has one; small enough to keep per message in flight.</summary>
    public static UpstreamOutcome? For(ConnectRequest message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return message.PayloadCase == ConnectRequest.PayloadOneofCase.InvitationRegistered
            && Guid.TryParse(message.InvitationRegistered.OrganizationId, out var organizationId)
            && Guid.TryParse(message.InvitationRegistered.InvitationId, out var invitationId)
            && message.InvitationRegistered.CodeVersion is > 0 and <= int.MaxValue
                ? new UpstreamOutcome(organizationId, invitationId, (int)message.InvitationRegistered.CodeVersion)
                : null;
    }

    /// <summary>
    /// Applies the outcome in its organization; <see langword="false"/> if it failed (the organization lock timed out,
    /// the database is unreachable), so the message is sent again later instead of being settled.
    /// </summary>
    [SuppressMessage("Design", "CA1031", Justification = "A failed outcome must not end the stream; the message is sent again and the outcome retried.")]
    public async Task<bool> ApplyAsync(UpstreamOutcome outcome, CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var services = scope.ServiceProvider;
            services.GetRequiredService<OrganizationContext>().Enter(outcome.OrganizationId);
            var session = services.GetRequiredService<DatabaseSession>();
            await session.BeginAsync(cancellationToken);
            // False (a resend replaced the version, revoked, already marked) is settled like true: nothing is left to do.
            await services.GetRequiredService<IInvitationRegistration>().MarkRegisteredAsync(outcome.InvitationId, outcome.CodeVersion, cancellationToken);
            await session.CommitAsync(cancellationToken);
            return true;
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            LogOutcomeFailed(logger, outcome.InvitationId, exception.GetType().Name);
            return false;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Marking invitation {InvitationId} registered failed with {ExceptionType}; the registration is sent again.")]
    private static partial void LogOutcomeFailed(ILogger logger, Guid invitationId, string exceptionType);
}
