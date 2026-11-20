using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Messaging;
using Backroom.Modules.Email.Application.Deliveries;
using Backroom.Modules.Email.Application.Sending;
using Backroom.Modules.Email.Application.Templates;
using Backroom.Modules.Organization.Contracts;

namespace Backroom.Modules.Email.Application.Invitations;

/// <summary>
/// <c>organization.invitation-ready.v1</c> → the invitation email (docs/contracts/phase-1.md, flow 3, step 3), in the
/// event's unit of work, which is in its organization. The link is read for the event's code version; if a resend
/// replaced it, or the invitation is no longer pending, nothing is sent (the newer version has its own event).
/// <para>
/// At most one email per invitation and code version: the inbox skips a redelivered event, and the delivery log's
/// reference stops a second send when the inbox row was lost after a send (the event's transaction failed to commit).
/// A delivery left <c>pending</c> means the outcome is unknown (the connection broke after the message started to be
/// handed over), so it is never resent. Only a send that failed before the hand-over is tried again: an unavailable
/// server through the event's retries, a shutdown through the event's redelivery (the row is <c>failed</c>).
/// </para>
/// <para>
/// This handler is the one that makes an external call (SMTP) while the event's transaction is open. The dispatcher
/// records the delivery in its own transactions, so the outcome survives a rollback of the event's; the open
/// transaction holds one pooled connection per queue (one delivery at a time) for at most the send timeout.
/// </para>
/// </summary>
internal sealed partial class InvitationReadyConsumer(
    IInvitationLinks links,
    EmailDispatcher dispatcher,
    ILogger<InvitationReadyConsumer> logger) : IIntegrationEventHandler<InvitationReady>
{
    public async Task HandleAsync(InvitationReady integrationEvent, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(integrationEvent);
        var invitationId = integrationEvent.InvitationId;
        var codeVersion = integrationEvent.CodeVersion;
        if (await links.GetLinkAsync(invitationId, codeVersion, cancellationToken) is not { } link)
        {
            LogNotCurrent(logger, invitationId, codeVersion, integrationEvent.OrgId);
            return;
        }

        var message = InvitationEmail.Create(link, integrationEvent.Locale);
        var outcome = await dispatcher.SendOnceAsync(
            integrationEvent.OrgId, InvitationEmail.Template, new EmailDeliveryReference(invitationId, codeVersion), message, cancellationToken);
        if (outcome is null)
        {
            LogAlreadyDelivered(logger, invitationId, codeVersion, integrationEvent.OrgId);
            return;
        }

        if (outcome.Failure == EmailSendFailure.Unavailable)
        {
            // Rolls back the event's transaction (the inbox row), so the consumer retries it after a delay. The failed
            // delivery does not block the retry. No address, link or server text in the message.
            throw new InvalidOperationException("The email server was unavailable; the invitation email is retried with the event.");
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Invitation {InvitationId} code version {CodeVersion} in organization {OrganizationId} gets no email: it is no longer pending, has expired or was replaced.")]
    private static partial void LogNotCurrent(ILogger logger, Guid invitationId, int codeVersion, Guid organizationId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Invitation {InvitationId} code version {CodeVersion} in organization {OrganizationId} is not emailed again: an earlier delivery is pending or sent.")]
    private static partial void LogAlreadyDelivered(ILogger logger, Guid invitationId, int codeVersion, Guid organizationId);
}
