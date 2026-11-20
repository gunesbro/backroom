namespace Backroom.Modules.Email.Application.Sending;

/// <summary>
/// Why a send did not end as sent. The caller may try again later for <see cref="Unavailable"/> and
/// <see cref="Cancelled"/>, never for <see cref="Unknown"/>; nothing is ever rerouted.
/// </summary>
internal enum EmailSendFailure
{
    /// <summary>
    /// The server could not be reached, or the connection broke before the message was handed over (before
    /// <c>DATA</c>/<c>BDAT</c>), so the server cannot have it (Mailpit down, for example). Retryable.
    /// </summary>
    Unavailable,

    /// <summary>The server refused the sender, a recipient or the message (an SMTP error reply). Not retried.</summary>
    Rejected,

    /// <summary>The configured capture server did not identify itself as Mailpit, so nothing was sent to it.</summary>
    NotMailpit,

    /// <summary>
    /// The connection broke or the send was cancelled after the message started to be handed over, before the
    /// server's reply arrived: the server may have accepted it. The delivery stays <c>pending</c> and is never resent,
    /// because a resend could deliver it twice.
    /// </summary>
    Unknown,

    /// <summary>Cancelled (shutdown) before the message was handed over, so nothing was sent. Retryable.</summary>
    Cancelled,
}
