namespace Backroom.Modules.Email.Application.Sending;

/// <summary>
/// The email provider interface (root CLAUDE.md, "Architecture rules"). Adapters live in the module's
/// infrastructure, the only place allowed to use mail libraries. Which server an adapter talks to is decided
/// once at startup by the safeguards of docs/adr/0008, never per call: outside Production (and in Production
/// without the explicit allow setting) every message goes to Mailpit.
/// </summary>
internal interface IEmailSender
{
    /// <summary>
    /// Sends one message. Failures are returned, not thrown, and never fall back to another server. Cancellation is
    /// returned too, as <see cref="EmailSendFailure.Cancelled"/> before the message was handed over or
    /// <see cref="EmailSendFailure.Unknown"/> after, so the caller can record what happened before it stops; once the
    /// server accepted the message the result is sent, whatever happens to the connection afterwards. The call is an
    /// external one: never make it inside the delivery log's unit of work.
    /// </summary>
    Task<EmailSendResult> SendAsync(EmailMessage message, CancellationToken cancellationToken);
}
