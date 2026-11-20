namespace Backroom.Modules.Email.Application.Sending;

/// <summary>The outcome of one send: the provider's message id, or why it failed (no server text, which can contain addresses).</summary>
internal readonly record struct EmailSendResult(string? ProviderMessageId, EmailSendFailure? Failure)
{
    public bool Succeeded => Failure is null;

    public static EmailSendResult Sent(string providerMessageId) => new(providerMessageId, null);

    public static EmailSendResult Failed(EmailSendFailure failure) => new(null, failure);
}
