namespace Backroom.Modules.Messaging.Presentation;

/// <summary>
/// One emoji of a message's reaction summary (docs/contracts/phase-2.md, flow 2 "Reactions"): the <c>Count</c> of people, whether
/// the reader is one (<c>ReactedByMe</c>) and up to three of them, the earliest first (<c>Sample</c>).
/// </summary>
public sealed record ReactionResponse(string Emoji, int Count, bool ReactedByMe, IReadOnlyList<Guid> Sample);
