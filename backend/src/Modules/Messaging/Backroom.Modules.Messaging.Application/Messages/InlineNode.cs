namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// An inline node of a block (docs/contracts/phase-2.md, "Block schema"): a run of text with its marks, or, when
/// <paramref name="MentionAccountId"/> is set, a mention of that account (then the other members are empty).
/// </summary>
internal sealed record InlineNode(string Text, TextMarks Marks, string? Href, Guid? MentionAccountId)
{
    public bool IsMention => MentionAccountId is not null;
}
