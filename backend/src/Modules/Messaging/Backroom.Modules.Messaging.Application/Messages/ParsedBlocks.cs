namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// Validated blocks in their canonical form, with what the server derives from them besides the text: the accounts
/// mentioned (each once, in the order of their first mention) and the <c>http</c> and <c>https</c> addresses linked
/// (each once, in order).
/// </summary>
internal sealed record ParsedBlocks(IReadOnlyList<BlockNode> Blocks, IReadOnlyList<Guid> MentionedAccountIds, IReadOnlyList<MessageLink> Links);
