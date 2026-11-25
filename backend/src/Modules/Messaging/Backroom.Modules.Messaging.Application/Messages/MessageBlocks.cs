using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The rich text of a message in our own JSON schema (docs/contracts/phase-2.md, flow 2, "Block schema (exact)").
/// The schema is closed: a node type, a mark or a member that is not listed is refused, never dropped, and the reason
/// names the place (<c>messaging.message_invalid</c> with a fixed <c>reason</c> token). The nesting is fixed by the
/// schema (array, block, list item, inline), so no input can go deeper than the walk below, and the size is bounded
/// by <see cref="MaxJsonBytes"/> before anything is read.
/// <para>
/// The canonical form is what is stored and sent on: neighbouring text nodes with equal marks are merged and an
/// absent <c>marks</c> stays absent. The text of a node is kept exactly as received (never trimmed or normalized);
/// only the derived plain text is, once, as a whole (<see cref="DeriveText"/> and <see cref="MessageText.TryPrepare"/>).
/// Nothing here is HTML: clients render nodes as text and build links only from validated addresses.
/// </para>
/// </summary>
internal static class MessageBlocks
{
    /// <summary>The largest <c>blocks</c> value, in UTF-8 bytes as received in the request body.</summary>
    public const int MaxJsonBytes = 32 * 1024;

    /// <summary>The longest link address, in characters.</summary>
    public const int MaxHrefLength = 2048;

    /// <summary>The most mention nodes in one message.</summary>
    public const int MaxMentions = 50;

    /// <summary>The longest host name a link can have (DNS).</summary>
    public const int MaxHostLength = 253;

    public const string Paragraph = "paragraph";
    public const string Quote = "quote";
    public const string CodeBlock = "code_block";
    public const string BulletedList = "bulleted_list";
    public const string NumberedList = "numbered_list";
    public const string ListItem = "list_item";
    public const string Text = "text";
    public const string Mention = "mention";

    public static readonly Error MentionInvalid =
        Error.Validation("messaging.mention_invalid", "A mention names someone who is not an active member, or a group that does not exist.");

    public static readonly Error MentionLimit =
        Error.Validation("messaging.mention_limit", "A message can hold at most 50 mentions.");

    // The canonical order of the marks without an attribute; the link comes last.
    private static readonly (string Name, TextMarks Mark)[] s_marks =
    [
        ("bold", TextMarks.Bold), ("italic", TextMarks.Italic), ("strike", TextMarks.Strike), ("code", TextMarks.Code),
    ];

    private const string LinkMark = "link";
    private const int LinkRank = 4;

    /// <summary>
    /// Validates client-supplied <paramref name="value"/> and returns its canonical form, or the error to answer.
    /// Reads no data: whether the mentioned accounts exist is the caller's check.
    /// </summary>
    public static Error? Parse(JsonElement value, out ParsedBlocks? parsed)
    {
        parsed = null;

        // The raw bytes of the value in the request body, before any parsing of ours: neither our escaping nor the
        // canonical form can change what is counted.
        if (JsonMarshal.GetRawUtf8Value(value).Length > MaxJsonBytes)
        {
            return MessageService.MessageInvalid.WithReason("blocks.size");
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            return MessageService.MessageInvalid.WithReason("blocks");
        }

        var parser = new Parser();
        var blocks = new List<BlockNode>(Math.Min(value.GetArrayLength(), 64));
        try
        {
            foreach (var item in value.EnumerateArray())
            {
                if (parser.Block(item) is not { } block)
                {
                    return MessageService.MessageInvalid.WithReason(parser.Reason ?? "blocks");
                }

                blocks.Add(block);
            }
        }
        catch (InvalidOperationException)
        {
            // The body was valid JSON, but a string in it is not text: an escaped lone surrogate, which the JSON
            // reader only finds when the string is read or compared.
            return MessageService.MessageInvalid.WithReason("blocks.encoding");
        }

        if (blocks.Count == 0)
        {
            return MessageService.MessageInvalid.WithReason("blocks.empty");
        }

        // A blank line is an empty paragraph between other blocks; a message never starts or ends with one.
        if (IsBlankParagraph(blocks[0]) || IsBlankParagraph(blocks[^1]))
        {
            return MessageService.MessageInvalid.WithReason("paragraph.blank_edge");
        }

        if (parser.MentionCount > MaxMentions)
        {
            return MentionLimit;
        }

        // Group mentions (everyone, here, named groups) arrive with user groups (step 31): no group exists yet, and
        // the special ones need permissions and the feed. Until then they are refused, not stored unchecked.
        if (parser.HasGroupMention)
        {
            return MentionInvalid.WithReason("group_mentions_unavailable");
        }

        parsed = new ParsedBlocks(blocks, parser.Accounts, parser.Links);
        return null;
    }

    /// <summary>The blocks of a plain text: one paragraph holding it as one text node (Phase 1's shape).</summary>
    public static ParsedBlocks FromPlainText(string text) =>
        new([new BlockNode(Paragraph, [new InlineNode(text, TextMarks.None, null, null)], null)], [], []);

    /// <summary>
    /// The plain text before trimming and normalization: one line per paragraph, quote, code block and list item, no
    /// markers, no link addresses; a mention is <c>@</c> and the member's name from <paramref name="names"/>.
    /// </summary>
    public static string DeriveText(ParsedBlocks blocks, IReadOnlyDictionary<Guid, string> names)
    {
        var text = new StringBuilder(256);
        var first = true;
        foreach (var block in blocks.Blocks)
        {
            if (block.Items is { } items)
            {
                foreach (var item in items)
                {
                    AppendLine(text, item, names, ref first);
                }
            }
            else
            {
                AppendLine(text, block.Children, names, ref first);
            }
        }

        return text.ToString();
    }

    /// <summary>The canonical JSON of the blocks, as stored.</summary>
    public static string ToJson(IReadOnlyList<BlockNode> blocks)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartArray();
            foreach (var block in blocks)
            {
                writer.WriteStartObject();
                writer.WriteString("type", block.Type);
                writer.WriteStartArray("children");
                if (block.Items is { } items)
                {
                    foreach (var item in items)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", ListItem);
                        writer.WriteStartArray("children");
                        WriteInlines(writer, item);
                        writer.WriteEndArray();
                        writer.WriteEndObject();
                    }
                }
                else
                {
                    WriteInlines(writer, block.Children);
                }

                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>The blocks as maps and lists of plain values, the same shape as the JSON, for the live event's MessagePack payload.</summary>
    public static IReadOnlyList<object> ToLive(IReadOnlyList<BlockNode> blocks)
    {
        var live = new List<object>(blocks.Count);
        foreach (var block in blocks)
        {
            List<object> children;
            if (block.Items is { } items)
            {
                children = new List<object>(items.Count);
                foreach (var item in items)
                {
                    children.Add(new Dictionary<string, object>(2, StringComparer.Ordinal) { ["type"] = ListItem, ["children"] = LiveInlines(item) });
                }
            }
            else
            {
                children = LiveInlines(block.Children);
            }

            live.Add(new Dictionary<string, object>(2, StringComparer.Ordinal) { ["type"] = block.Type, ["children"] = children });
        }

        return live;
    }

    private static void AppendLine(StringBuilder text, IReadOnlyList<InlineNode> inlines, IReadOnlyDictionary<Guid, string> names, ref bool first)
    {
        if (!first)
        {
            text.Append('\n');
        }

        first = false;
        foreach (var inline in inlines)
        {
            if (inline.MentionAccountId is { } accountId)
            {
                text.Append('@').Append(names[accountId]);
            }
            else
            {
                text.Append(inline.Text);
            }
        }
    }

    private static void WriteInlines(Utf8JsonWriter writer, IReadOnlyList<InlineNode> inlines)
    {
        foreach (var inline in inlines)
        {
            writer.WriteStartObject();
            if (inline.MentionAccountId is { } accountId)
            {
                writer.WriteString("type", Mention);
                writer.WriteString("accountId", accountId);
            }
            else
            {
                writer.WriteString("type", Text);
                writer.WriteString("text", inline.Text);
                if (inline.Marks != TextMarks.None || inline.Href is not null)
                {
                    writer.WriteStartArray("marks");
                    foreach (var (name, mark) in s_marks)
                    {
                        if ((inline.Marks & mark) != 0)
                        {
                            writer.WriteStartObject();
                            writer.WriteString("type", name);
                            writer.WriteEndObject();
                        }
                    }

                    if (inline.Href is { } href)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("type", LinkMark);
                        writer.WriteString("href", href);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                }
            }

            writer.WriteEndObject();
        }
    }

    private static List<object> LiveInlines(IReadOnlyList<InlineNode> inlines)
    {
        var live = new List<object>(inlines.Count);
        foreach (var inline in inlines)
        {
            if (inline.MentionAccountId is { } accountId)
            {
                live.Add(new Dictionary<string, object>(2, StringComparer.Ordinal) { ["type"] = Mention, ["accountId"] = accountId.ToString("D") });
                continue;
            }

            var node = new Dictionary<string, object>(3, StringComparer.Ordinal) { ["type"] = Text, ["text"] = inline.Text };
            if (inline.Marks != TextMarks.None || inline.Href is not null)
            {
                var marks = new List<object>(2);
                foreach (var (name, mark) in s_marks)
                {
                    if ((inline.Marks & mark) != 0)
                    {
                        marks.Add(new Dictionary<string, object>(1, StringComparer.Ordinal) { ["type"] = name });
                    }
                }

                if (inline.Href is { } href)
                {
                    marks.Add(new Dictionary<string, object>(2, StringComparer.Ordinal) { ["type"] = LinkMark, ["href"] = href });
                }

                node["marks"] = marks;
            }

            live.Add(node);
        }

        return live;
    }

    private static bool IsBlankParagraph(BlockNode block)
    {
        if (!string.Equals(block.Type, Paragraph, StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var inline in block.Children)
        {
            if (inline.IsMention || !string.IsNullOrWhiteSpace(inline.Text))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// A link address: only ASCII without spaces or controls (the URL parser's canonical form is that), an absolute
    /// <c>https</c>, <c>http</c> or <c>mailto</c> address in lower-case scheme, at most <see cref="MaxHrefLength"/>
    /// characters. <paramref name="host"/> is set for web addresses. Everything else (<c>javascript:</c>,
    /// <c>data:</c>, relative addresses, a scheme hidden behind a tab) is refused.
    /// </summary>
    internal static bool TryReadHref(string href, out string? host)
    {
        host = null;
        if (href.Length is 0 or > MaxHrefLength)
        {
            return false;
        }

        foreach (var character in href)
        {
            if (character is <= ' ' or >= (char)0x7F)
            {
                return false;
            }
        }

        if (href.StartsWith("mailto:", StringComparison.Ordinal))
        {
            return href.Length > "mailto:".Length;
        }

        if (!href.StartsWith("https://", StringComparison.Ordinal) && !href.StartsWith("http://", StringComparison.Ordinal))
        {
            return false;
        }

        if (!Uri.TryCreate(href, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http") || uri.Host.Length == 0)
        {
            return false;
        }

        var name = uri.IdnHost.ToLowerInvariant();
        if (name.Length is 0 or > MaxHostLength)
        {
            return false;
        }

        host = name;
        return true;
    }

    /// <summary>One walk over a client's blocks; <see cref="Reason"/> names the first thing that was refused.</summary>
    private sealed class Parser
    {
        private readonly List<Guid> _accounts = [];
        private readonly List<MessageLink> _links = [];
        private HashSet<string>? _linked;

        public string? Reason { get; private set; }

        public int MentionCount { get; private set; }

        public bool HasGroupMention { get; private set; }

        public IReadOnlyList<Guid> Accounts => _accounts;

        public IReadOnlyList<MessageLink> Links => _links;

        public BlockNode? Block(JsonElement element)
        {
            if (!TryReadNode(element, "block", out var type, out var children))
            {
                return null;
            }

            switch (type)
            {
                case Paragraph:
                case Quote:
                case CodeBlock:
                    if (Inlines(children, inCode: type == CodeBlock) is not { } inlines)
                    {
                        return null;
                    }

                    // Only a paragraph may be empty (a blank line).
                    return inlines.Count == 0 && type != Paragraph ? Fail<BlockNode>(type + ".empty") : new BlockNode(type, inlines, null);

                case BulletedList:
                case NumberedList:
                    var items = new List<IReadOnlyList<InlineNode>>(Math.Min(children.GetArrayLength(), 64));
                    foreach (var item in children.EnumerateArray())
                    {
                        if (!TryReadNode(item, ListItem, out var itemType, out var itemChildren))
                        {
                            return null;
                        }

                        // No nested list, no block inside an item: only list items, holding inline nodes.
                        if (itemType != ListItem)
                        {
                            return Fail<BlockNode>("list.children");
                        }

                        if (Inlines(itemChildren, inCode: false) is not { } itemInlines)
                        {
                            return null;
                        }

                        if (itemInlines.Count == 0)
                        {
                            return Fail<BlockNode>("list_item.empty");
                        }

                        items.Add(itemInlines);
                    }

                    return items.Count == 0 ? Fail<BlockNode>("list.empty") : new BlockNode(type, [], items);

                default:
                    return Fail<BlockNode>("block.type");
            }
        }

        /// <summary>A block or a list item: an object with exactly a <c>type</c> string and a <c>children</c> array.</summary>
        private bool TryReadNode(JsonElement element, string name, out string type, out JsonElement children)
        {
            type = string.Empty;
            children = default;
            if (element.ValueKind != JsonValueKind.Object)
            {
                Reason = name;
                return false;
            }

            string? foundType = null;
            var hasChildren = false;
            foreach (var member in element.EnumerateObject())
            {
                if (member.NameEquals("type"))
                {
                    if (foundType is not null || member.Value.ValueKind != JsonValueKind.String)
                    {
                        Reason = name + ".type";
                        return false;
                    }

                    foundType = member.Value.GetString();
                }
                else if (member.NameEquals("children"))
                {
                    if (hasChildren || member.Value.ValueKind != JsonValueKind.Array)
                    {
                        Reason = name + ".children";
                        return false;
                    }

                    hasChildren = true;
                    children = member.Value;
                }
                else
                {
                    Reason = name + ".unknown_member";
                    return false;
                }
            }

            if (foundType is null)
            {
                Reason = name + ".type";
                return false;
            }

            if (!hasChildren)
            {
                Reason = name + ".children";
                return false;
            }

            type = foundType;
            return true;
        }

        private List<InlineNode>? Inlines(JsonElement children, bool inCode)
        {
            var inlines = new List<InlineNode>(Math.Min(children.GetArrayLength(), 64));
            foreach (var element in children.EnumerateArray())
            {
                if (Inline(element, inCode) is not { } inline)
                {
                    return null;
                }

                // Canonical: neighbouring text nodes with equal marks are one node.
                if (!inline.IsMention && inlines.Count > 0 && inlines[^1] is { IsMention: false } last
                    && last.Marks == inline.Marks && string.Equals(last.Href, inline.Href, StringComparison.Ordinal))
                {
                    inlines[^1] = last with { Text = last.Text + inline.Text };
                }
                else
                {
                    inlines.Add(inline);
                }
            }

            return inlines;
        }

        private InlineNode? Inline(JsonElement element, bool inCode)
        {
            if (element.ValueKind != JsonValueKind.Object)
            {
                return Fail<InlineNode>("inline");
            }

            // Every member a text or a mention node can have, each at most once; anything else is refused.
            JsonElement type = default, text = default, marks = default, accountId = default, groupId = default, special = default;
            foreach (var member in element.EnumerateObject())
            {
                ref var slot = ref type;
                if (member.NameEquals("type"))
                {
                    slot = ref type;
                }
                else if (member.NameEquals("text"))
                {
                    slot = ref text;
                }
                else if (member.NameEquals("marks"))
                {
                    slot = ref marks;
                }
                else if (member.NameEquals("accountId"))
                {
                    slot = ref accountId;
                }
                else if (member.NameEquals("groupId"))
                {
                    slot = ref groupId;
                }
                else if (member.NameEquals("special"))
                {
                    slot = ref special;
                }
                else
                {
                    return Fail<InlineNode>("inline.unknown_member");
                }

                if (slot.ValueKind != JsonValueKind.Undefined)
                {
                    return Fail<InlineNode>("inline.duplicate_member");
                }

                slot = member.Value;
            }

            if (type.ValueKind != JsonValueKind.String)
            {
                return Fail<InlineNode>("inline.type");
            }

            if (type.ValueEquals(Text))
            {
                return Has(accountId) || Has(groupId) || Has(special)
                    ? Fail<InlineNode>("text.unknown_member")
                    : TextNode(text, marks, inCode);
            }

            if (type.ValueEquals(Mention))
            {
                return Has(text) || Has(marks) ? Fail<InlineNode>("mention.unknown_member") : MentionNode(accountId, groupId, special);
            }

            return Fail<InlineNode>("inline.type");
        }

        private InlineNode? TextNode(JsonElement text, JsonElement marks, bool inCode)
        {
            // Kept as received. No carriage return: a line break inside a block is a line feed.
            if (text.ValueKind != JsonValueKind.String || text.GetString() is not { Length: > 0 } value
                || value.Contains('\r', StringComparison.Ordinal) || !TextRules.IsClean(value, allowLayoutControls: true))
            {
                return Fail<InlineNode>("text.text");
            }

            if (!Has(marks))
            {
                return new InlineNode(value, TextMarks.None, null, null);
            }

            if (inCode)
            {
                return Fail<InlineNode>("code_block.marks");
            }

            // Present means not empty, each type once, in the canonical order.
            if (marks.ValueKind != JsonValueKind.Array || marks.GetArrayLength() == 0)
            {
                return Fail<InlineNode>("text.marks");
            }

            var found = TextMarks.None;
            string? href = null;
            var lastRank = -1;
            foreach (var mark in marks.EnumerateArray())
            {
                if (!TryReadMark(mark, out var rank, out var markHref) || rank <= lastRank)
                {
                    Reason ??= "text.marks";
                    return null;
                }

                lastRank = rank;
                if (rank == LinkRank)
                {
                    href = markHref;
                }
                else
                {
                    found |= s_marks[rank].Mark;
                }
            }

            return new InlineNode(value, found, href, null);
        }

        private bool TryReadMark(JsonElement mark, out int rank, out string? href)
        {
            rank = -1;
            href = null;
            if (mark.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            JsonElement type = default, address = default;
            foreach (var member in mark.EnumerateObject())
            {
                if (member.NameEquals("type") && type.ValueKind == JsonValueKind.Undefined)
                {
                    type = member.Value;
                }
                else if (member.NameEquals("href") && address.ValueKind == JsonValueKind.Undefined)
                {
                    address = member.Value;
                }
                else
                {
                    Reason = "mark.unknown_member";
                    return false;
                }
            }

            if (type.ValueKind != JsonValueKind.String)
            {
                Reason = "mark.type";
                return false;
            }

            if (type.ValueEquals(LinkMark))
            {
                if (address.ValueKind != JsonValueKind.String || address.GetString() is not { } value || !TryReadHref(value, out var host))
                {
                    Reason = "link.href";
                    return false;
                }

                rank = LinkRank;
                href = value;
                if (host is not null && (_linked ??= new HashSet<string>(StringComparer.Ordinal)).Add(value))
                {
                    _links.Add(new MessageLink(value, host));
                }

                return true;
            }

            // Only a link has an attribute.
            if (Has(address))
            {
                Reason = "mark.unknown_member";
                return false;
            }

            for (var index = 0; index < s_marks.Length; index++)
            {
                if (type.ValueEquals(s_marks[index].Name))
                {
                    rank = index;
                    return true;
                }
            }

            Reason = "mark.type";
            return false;
        }

        private InlineNode? MentionNode(JsonElement accountId, JsonElement groupId, JsonElement special)
        {
            // Exactly one of the three.
            if ((Has(accountId) ? 1 : 0) + (Has(groupId) ? 1 : 0) + (Has(special) ? 1 : 0) != 1)
            {
                return Fail<InlineNode>("mention");
            }

            MentionCount++;
            if (Has(special))
            {
                if (special.ValueKind != JsonValueKind.String || !(special.ValueEquals("everyone") || special.ValueEquals("here")))
                {
                    return Fail<InlineNode>("mention.special");
                }

                HasGroupMention = true;
                return new InlineNode(string.Empty, TextMarks.None, null, Guid.Empty);
            }

            var isGroup = Has(groupId);
            var id = isGroup ? groupId : accountId;
            if (id.ValueKind != JsonValueKind.String || !Guid.TryParseExact(id.GetString(), "D", out var parsedId) || parsedId == Guid.Empty)
            {
                return Fail<InlineNode>(isGroup ? "mention.group_id" : "mention.account_id");
            }

            if (isGroup)
            {
                HasGroupMention = true;
                return new InlineNode(string.Empty, TextMarks.None, null, Guid.Empty);
            }

            // At most MaxMentions distinct accounts get this far before the caller stops, so the scan stays short.
            if (_accounts.Count <= MaxMentions && !_accounts.Contains(parsedId))
            {
                _accounts.Add(parsedId);
            }

            return new InlineNode(string.Empty, TextMarks.None, null, parsedId);
        }

        private static bool Has(JsonElement element) => element.ValueKind != JsonValueKind.Undefined;

        private T? Fail<T>(string reason)
            where T : class
        {
            Reason = reason;
            return null;
        }
    }
}
