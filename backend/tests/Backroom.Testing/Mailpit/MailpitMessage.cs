namespace Backroom.Testing.Mailpit;

/// <summary>A message as Mailpit's search lists it.</summary>
public sealed record MailpitMessage(string Id, string MessageId, string Subject);
