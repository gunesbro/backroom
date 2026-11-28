namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The flag <c>Features:MessageReply</c> (docs/contracts/phase-2.md, flow 2 "Reply with quote"). Off (the default): a send
/// with <c>replyToMessageId</c> answers <c>403 messaging.message_reply_unavailable</c> and stores nothing; the id is never
/// stripped. It stays off until the web renders quotes (including the deleted one); then it is turned on and removed.
/// </summary>
internal sealed record MessageReply(bool Enabled)
{
    public const string Flag = "Features:MessageReply";
}
