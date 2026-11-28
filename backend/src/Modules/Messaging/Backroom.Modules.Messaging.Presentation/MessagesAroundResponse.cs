namespace Backroom.Modules.Messaging.Presentation;

/// <summary>The messages around one message, newest first (docs/contracts/phase-2.md, "Jump to a message"); <c>Before</c> and <c>After</c> are the cursors of the following reads.</summary>
public sealed record MessagesAroundResponse(IReadOnlyList<MessageResponse> Items, long? Before, long? After, MessageAnchorResponse Anchor, string Epoch);

/// <summary>The message the read was about.</summary>
public sealed record MessageAnchorResponse(Guid Id, long Seq);
