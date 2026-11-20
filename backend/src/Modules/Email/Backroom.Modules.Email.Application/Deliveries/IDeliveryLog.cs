namespace Backroom.Modules.Email.Application.Deliveries;

/// <summary>
/// The delivery log in the current unit of work, which row-level security limits to the entered organization.
/// It holds the template, the status, a keyed hash of the recipient and optionally the ids the delivery is for:
/// never an address, a subject, a body or a link.
/// </summary>
internal interface IDeliveryLog
{
    /// <summary>
    /// Records a pending delivery; <see langword="false"/> (nothing recorded) if <paramref name="reference"/> is given
    /// and a delivery of the same template name for it is already pending or sent. A failed one does not count.
    /// </summary>
    Task<bool> AddPendingAsync(
        Guid deliveryId,
        Guid organizationId,
        EmailTemplateId template,
        EmailDeliveryReference? reference,
        byte[] recipientHash,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    /// <summary>Moves a pending delivery to <see cref="DeliveryStatus.Sent"/> or <see cref="DeliveryStatus.Failed"/>.</summary>
    Task CompleteAsync(Guid deliveryId, string status, string? providerMessageId, DateTimeOffset now, CancellationToken cancellationToken);
}
