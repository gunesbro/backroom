namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>A conversation after a change that took the next change number.</summary>
internal sealed record ConversationChange(long ChangeSeq, string? Name, string? Description, DateTimeOffset? ArchivedAt, int MemberCount);
