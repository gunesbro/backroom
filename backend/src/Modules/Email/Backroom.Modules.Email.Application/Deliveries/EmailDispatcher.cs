using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.Modules.Email.Application.Sending;

namespace Backroom.Modules.Email.Application.Deliveries;

/// <summary>
/// Sends one templated message for an organization and records it in the delivery log: the pending row is
/// committed first, then the message is sent outside the log's transactions (no external call inside its unit of
/// work), then the outcome is committed. A failed send is recorded and returned; it is never retried here and never
/// rerouted, so the caller (an event consumer) decides whether to try again later. An unknown outcome
/// (<see cref="EmailSendFailure.Unknown"/>: the server may have the message) leaves the row <c>pending</c>; on
/// cancellation the outcome is recorded first, then <see cref="OperationCanceledException"/> is thrown.
/// </summary>
internal sealed partial class EmailDispatcher(
    IServiceScopeFactory scopes,
    IEmailSender sender,
    IRecipientHasher recipientHasher,
    TimeProvider timeProvider,
    ILogger<EmailDispatcher> logger)
{
    public const string ActivitySourceName = "Backroom.Email";

    private static readonly ActivitySource s_activitySource = new(ActivitySourceName);

    public async Task<EmailDeliveryOutcome> SendAsync(Guid organizationId, EmailTemplateId template, EmailMessage message, CancellationToken cancellationToken) =>
        await SendCoreAsync(organizationId, template, reference: null, message, cancellationToken)
        ?? throw new InvalidOperationException("A delivery without a reference is always recorded.");

    /// <summary>
    /// Sends at most once per template name and <paramref name="reference"/>: <see langword="null"/>, and nothing is
    /// sent, if a delivery for it is already pending (its outcome is unknown, so it is never resent blindly) or sent.
    /// After a failed delivery a later call sends again.
    /// </summary>
    public Task<EmailDeliveryOutcome?> SendOnceAsync(
        Guid organizationId, EmailTemplateId template, EmailDeliveryReference reference, EmailMessage message, CancellationToken cancellationToken) =>
        SendCoreAsync(organizationId, template, reference, message, cancellationToken);

    private async Task<EmailDeliveryOutcome?> SendCoreAsync(
        Guid organizationId, EmailTemplateId template, EmailDeliveryReference? reference, EmailMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(message);
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("The organization id must not be empty.", nameof(organizationId));
        }

        // Ids and the template only: no address, subject or body on the span.
        using var activity = s_activitySource.StartActivity("email.send", ActivityKind.Internal);
        var deliveryId = Guid.CreateVersion7(timeProvider.GetUtcNow());
        activity?.SetTag("backroom.org_id", organizationId);
        activity?.SetTag("backroom.email.delivery_id", deliveryId);
        activity?.SetTag("backroom.email.template", template.Name);
        activity?.SetTag("backroom.email.template_version", template.Version);

        var recipientHash = recipientHasher.Hash(message.To);
        var recorded = false;
        await InOrganizationAsync(
            organizationId,
            async log => recorded = await log.AddPendingAsync(deliveryId, organizationId, template, reference, recipientHash, timeProvider.GetUtcNow(), cancellationToken),
            cancellationToken);
        if (!recorded)
        {
            activity?.SetTag("backroom.email.status", "duplicate");
            LogDuplicate(logger, organizationId, template.Name, template.Version);
            return null;
        }

        var result = await sender.SendAsync(message, cancellationToken);

        // An unknown outcome (the server may have the message) keeps the row pending, so nothing ever resends it.
        // Everything else is recorded, not cancellable, so the outcome is kept even during shutdown.
        var status = result.Failure switch
        {
            null => DeliveryStatus.Sent,
            EmailSendFailure.Unknown => DeliveryStatus.Pending,
            _ => DeliveryStatus.Failed,
        };
        if (status != DeliveryStatus.Pending)
        {
            await InOrganizationAsync(organizationId, log => log.CompleteAsync(deliveryId, status, result.ProviderMessageId, timeProvider.GetUtcNow(), CancellationToken.None), CancellationToken.None);
        }

        activity?.SetTag("backroom.email.status", status);
        if (result.Failure is { } failure)
        {
            activity?.SetStatus(ActivityStatusCode.Error, failure.ToString());
            LogFailed(logger, deliveryId, organizationId, template.Name, template.Version, failure);
        }
        else
        {
            LogSent(logger, deliveryId, organizationId, template.Name, template.Version);
        }

        // Shutdown: the outcome is recorded; the caller stops (an event goes back to its queue). A cancelled send left
        // a failed row, so the redelivery sends; an unknown one left a pending row, so the redelivery does not.
        cancellationToken.ThrowIfCancellationRequested();
        return new EmailDeliveryOutcome(deliveryId, status, result.Failure);
    }

    private async Task InOrganizationAsync(Guid organizationId, Func<IDeliveryLog, Task> action, CancellationToken cancellationToken)
    {
        await using var scope = scopes.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(cancellationToken);
        await action(scope.ServiceProvider.GetRequiredService<IDeliveryLog>());
        await unitOfWork.CommitAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Email {DeliveryId} for organization {OrganizationId} sent ({Template} v{TemplateVersion}).")]
    private static partial void LogSent(ILogger logger, Guid deliveryId, Guid organizationId, string template, int templateVersion);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Email {DeliveryId} for organization {OrganizationId} failed ({Template} v{TemplateVersion}): {Failure}.")]
    private static partial void LogFailed(ILogger logger, Guid deliveryId, Guid organizationId, string template, int templateVersion, EmailSendFailure failure);

    [LoggerMessage(Level = LogLevel.Information, Message = "Email for organization {OrganizationId} not sent ({Template} v{TemplateVersion}): a delivery for the same reference is already pending or sent.")]
    private static partial void LogDuplicate(ILogger logger, Guid organizationId, string template, int templateVersion);
}
