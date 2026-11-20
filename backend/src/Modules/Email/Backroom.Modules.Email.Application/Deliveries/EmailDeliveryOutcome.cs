using Backroom.Modules.Email.Application.Sending;

namespace Backroom.Modules.Email.Application.Deliveries;

/// <summary>The recorded delivery: its id in the log, its status (<c>pending</c> if the outcome is unknown) and, if it did not succeed, why.</summary>
internal sealed record EmailDeliveryOutcome(Guid DeliveryId, string Status, EmailSendFailure? Failure);
