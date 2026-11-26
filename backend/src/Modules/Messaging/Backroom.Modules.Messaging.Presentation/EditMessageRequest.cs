using System.Text.Json;

namespace Backroom.Modules.Messaging.Presentation;

/// <summary>An edit (docs/contracts/phase-2.md, "HTTP APIs"): the new content as <c>Blocks</c> in the block schema; the server derives the text.</summary>
public sealed record EditMessageRequest(JsonElement? Blocks);
