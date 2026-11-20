namespace Backroom.Modules.Email.Application.Deliveries;

/// <summary>A delivery's status in <c>email.deliveries</c>.</summary>
internal static class DeliveryStatus
{
    /// <summary>Recorded before the send; stays so if the process stops before the outcome is written.</summary>
    public const string Pending = "pending";

    /// <summary>The server accepted the message.</summary>
    public const string Sent = "sent";

    public const string Failed = "failed";
}
