namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// A message as live clients receive it. <paramref name="Text"/> is the plain text (never escaped; clients escape when
/// they render). <paramref name="Seq"/> is its place in the channel in commit order: clients resume with <c>after={lastSeenSeq}</c>.
/// </summary>
public sealed record LiveMessage(Guid Id, long Seq, Guid ClientMessageId, Guid AuthorAccountId, string Text, DateTimeOffset CreatedAt);
