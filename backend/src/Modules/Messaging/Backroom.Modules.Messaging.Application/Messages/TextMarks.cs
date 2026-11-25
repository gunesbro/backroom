namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>The marks of a text node that have no attribute, in their canonical order; a link is the node's <c>Href</c>.</summary>
[Flags]
internal enum TextMarks
{
    None = 0,
    Bold = 1,
    Italic = 2,
    Strike = 4,
    Code = 8,
}
