namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// A block of a message (docs/contracts/phase-2.md, "Block schema"). A <c>paragraph</c>, <c>quote</c> or
/// <c>code_block</c> has <paramref name="Children"/>; a list has <paramref name="Items"/>, the children of each
/// <c>list_item</c>, and no children of its own.
/// </summary>
internal sealed record BlockNode(string Type, IReadOnlyList<InlineNode> Children, IReadOnlyList<IReadOnlyList<InlineNode>>? Items);
