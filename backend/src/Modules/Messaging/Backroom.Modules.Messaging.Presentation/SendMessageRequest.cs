using System.Text.Json;

namespace Backroom.Modules.Messaging.Presentation;

/// <summary>
/// A send (docs/contracts/phase-2.md, "HTTP APIs"): the client id with either <c>Blocks</c> (rich text in the block
/// schema; the server validates them and derives the text, so a <c>Text</c> sent along is not read) or plain
/// <c>Text</c> (the server derives the blocks).
/// </summary>
public sealed record SendMessageRequest(Guid? ClientMessageId, string? Text, JsonElement? Blocks, Guid? ReplyToMessageId = null);
