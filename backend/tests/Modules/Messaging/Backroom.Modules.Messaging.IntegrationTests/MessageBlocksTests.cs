using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using MessagePack;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Application.Messages;
using Backroom.Testing;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>The block schema of docs/contracts/phase-2.md, flow 2 ("Block schema (exact)"): closed, canonical, bounded.</summary>
public sealed class MessageBlocksTests
{
    private static readonly string s_decomposed = "cafe" + (char)0x301;

    private const string Everything = """
        [{"type":"paragraph","children":[{"type":"text","text":"plain "},{"type":"text","text":"all","marks":[{"type":"bold"},{"type":"italic"},{"type":"strike"},{"type":"code"},{"type":"link","href":"https://example.test/a"}]}]},
         {"type":"paragraph","children":[]},
         {"type":"quote","children":[{"type":"text","text":"quoted\nline"}]},
         {"type":"code_block","children":[{"type":"text","text":"if (a < b) {\n\treturn;\n}"}]},
         {"type":"bulleted_list","children":[{"type":"list_item","children":[{"type":"text","text":"one"}]},{"type":"list_item","children":[{"type":"text","text":"two","marks":[{"type":"italic"}]}]}]},
         {"type":"numbered_list","children":[{"type":"list_item","children":[{"type":"text","text":"first"}]}]},
         {"type":"paragraph","children":[{"type":"text","text":"mail","marks":[{"type":"link","href":"mailto:someone@example.test"}]}]}]
        """;

    [Fact]
    public void Parse_WithEveryBlockAndMark_KeepsTheCanonicalShape()
    {
        var (error, parsed) = Parse(Everything);

        error.Should().BeNull();
        JsonNode.DeepEquals(JsonNode.Parse(MessageBlocks.ToJson(parsed!.Blocks)), JsonNode.Parse(Everything)).Should().BeTrue();
        parsed.MentionedAccountIds.Should().BeEmpty();
        parsed.Links.Should().Equal(new MessageLink("https://example.test/a", "example.test"));
    }

    [Fact]
    public void Parse_UnderTheTurkishCulture_ReadsTheSameTypes()
    {
        using var turkish = CultureScope.Turkish();

        var (error, parsed) = Parse(Everything);

        error.Should().BeNull();
        JsonNode.DeepEquals(JsonNode.Parse(MessageBlocks.ToJson(parsed!.Blocks)), JsonNode.Parse(Everything)).Should().BeTrue();
    }

    [Fact]
    public void Parse_MergesNeighbouringTextNodesWithEqualMarksAndKeepsTheTextAsReceived()
    {
        var (error, parsed) = Parse($$"""
            [{"type":"paragraph","children":[
              {"type":"text","text":"  {{s_decomposed}} "},{"type":"text","text":"one"},
              {"type":"text","text":"b","marks":[{"type":"bold"}]},{"type":"text","text":"c","marks":[{"type":"bold"}]},
              {"type":"text","text":"l","marks":[{"type":"link","href":"https://a.test/"}]},{"type":"text","text":"m","marks":[{"type":"link","href":"https://b.test/"}]}]}]
            """);

        error.Should().BeNull();
        parsed!.Blocks.Should().ContainSingle().Which.Children.Select(node => (node.Text, node.Marks, node.Href)).Should().Equal(
            ("  " + s_decomposed + " one", TextMarks.None, null),
            ("bc", TextMarks.Bold, null),
            ("l", TextMarks.None, "https://a.test/"),
            ("m", TextMarks.None, "https://b.test/"));
        parsed.Links.Select(link => link.Host).Should().Equal("a.test", "b.test");
    }

    [Theory]
    [InlineData("""{"type":"paragraph","children":[]}""", "blocks")]
    [InlineData("\"text\"", "blocks")]
    [InlineData("[]", "blocks.empty")]
    [InlineData("""["text"]""", "block")]
    [InlineData("""[{"children":[]}]""", "block.type")]
    [InlineData("""[{"type":7,"children":[]}]""", "block.type")]
    [InlineData("""[{"type":"heading","children":[{"type":"text","text":"a"}]}]""", "block.type")]
    [InlineData("""[{"type":"table","children":[]}]""", "block.type")]
    [InlineData("""[{"type":"list_item","children":[{"type":"text","text":"a"}]}]""", "block.type")]
    [InlineData("""[{"type":"paragraph"}]""", "block.children")]
    [InlineData("""[{"type":"paragraph","children":"a"}]""", "block.children")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a"}],"align":"left"}]""", "block.unknown_member")]
    [InlineData("""[{"type":"paragraph","type":"quote","children":[{"type":"text","text":"a"}]}]""", "block.type")]
    [InlineData("""[{"type":"paragraph","children":[]}]""", "paragraph.blank_edge")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":" \n\t "}]},{"type":"paragraph","children":[{"type":"text","text":"a"}]}]""", "paragraph.blank_edge")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a"}]},{"type":"paragraph","children":[]}]""", "paragraph.blank_edge")]
    [InlineData("""[{"type":"quote","children":[]}]""", "quote.empty")]
    [InlineData("""[{"type":"code_block","children":[]}]""", "code_block.empty")]
    [InlineData("""[{"type":"bulleted_list","children":[]}]""", "list.empty")]
    [InlineData("""[{"type":"numbered_list","children":[{"type":"list_item","children":[]}]}]""", "list_item.empty")]
    [InlineData("""[{"type":"bulleted_list","children":[{"type":"text","text":"a"}]}]""", "list_item.unknown_member")]
    [InlineData("""[{"type":"bulleted_list","children":[{"type":"paragraph","children":[{"type":"text","text":"a"}]}]}]""", "list.children")]
    [InlineData("""[{"type":"bulleted_list","children":[{"type":"bulleted_list","children":[{"type":"list_item","children":[{"type":"text","text":"a"}]}]}]}]""", "list.children")]
    [InlineData("""[{"type":"bulleted_list","children":[{"type":"list_item","children":[{"type":"list_item","children":[]}]}]}]""", "inline.unknown_member")]
    [InlineData("""[{"type":"bulleted_list","children":[{"type":"list_item","children":[{"type":"text","text":"a"}],"checked":true}]}]""", "list_item.unknown_member")]
    [InlineData("""[{"type":"paragraph","children":["a"]}]""", "inline")]
    [InlineData("""[{"type":"paragraph","children":[{"text":"a"}]}]""", "inline.type")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"image","text":"a"}]}]""", "inline.type")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"line_break"}]}]""", "inline.type")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","color":"red"}]}]""", "inline.unknown_member")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","text":"b"}]}]""", "inline.duplicate_member")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","accountId":"0198a4c2-0000-7000-8000-000000000001"}]}]""", "text.unknown_member")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text"}]}]""", "text.text")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":""}]}]""", "text.text")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":7}]}]""", "text.text")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"bell\u0007"}]}]""", "text.text")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a\r\nb"}]}]""", "text.text")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"lone \ud83d"}]}]""", "blocks.encoding")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":[]}]}]""", "text.marks")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":null}]}]""", "text.marks")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":["bold"]}]}]""", "text.marks")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":[{"type":"bold"},{"type":"bold"}]}]}]""", "text.marks")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":[{"type":"italic"},{"type":"bold"}]}]}]""", "text.marks")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":[{"type":"underline"}]}]}]""", "mark.type")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":[{"type":"BOLD"}]}]}]""", "mark.type")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":[{"type":"bold","href":"https://a.test/"}]}]}]""", "mark.unknown_member")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":[{"type":"link","href":"https://a.test/","title":"x"}]}]}]""", "mark.unknown_member")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":[{"type":"link"}]}]}]""", "link.href")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"text","text":"a","marks":[{"type":"link","href":"javascript:alert(1)"}]}]}]""", "link.href")]
    [InlineData("""[{"type":"code_block","children":[{"type":"text","text":"a","marks":[{"type":"bold"}]}]}]""", "code_block.marks")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"mention"}]}]""", "mention")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"mention","accountId":"0198a4c2-0000-7000-8000-000000000001","special":"here"}]}]""", "mention")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"mention","accountId":"not-a-uuid"}]}]""", "mention.account_id")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"mention","accountId":"00000000-0000-0000-0000-000000000000"}]}]""", "mention.account_id")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"mention","groupId":7}]}]""", "mention.group_id")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"mention","special":"channel"}]}]""", "mention.special")]
    [InlineData("""[{"type":"paragraph","children":[{"type":"mention","accountId":"0198a4c2-0000-7000-8000-000000000001","marks":[{"type":"bold"}]}]}]""", "mention.unknown_member")]
    public void Parse_WithAnythingOutsideTheSchema_IsRefusedWithItsReason(string json, string reason)
    {
        var (error, parsed) = Parse(json);

        parsed.Should().BeNull();
        error!.Code.Should().Be("messaging.message_invalid");
        error.Kind.Should().Be(ErrorKind.Validation);
        error.Reason.Should().Be(reason);
    }

    [Fact]
    public void Parse_WithALineSeparatorOrAnEscapedLoneSurrogateInAName_IsRefused()
    {
        var separated = Parse("[{\"type\":\"paragraph\",\"children\":[{\"type\":\"text\",\"text\":\"a" + (char)0x2028 + "b\"}]}]");
        var name = Parse("""[{"type":"paragraph","children":[{"type":"text","te\ud83dxt":"a"}]}]""");

        separated.Error!.Reason.Should().Be("text.text");
        name.Blocks.Should().BeNull();
        name.Error!.Code.Should().Be("messaging.message_invalid");
    }

    [Fact]
    public void Parse_CountsTheSizeOnTheBytesAsReceived()
    {
        // The same blocks, padded with spaces the canonical form would drop, and with characters of three bytes each.
        const string Prefix = """[{"type":"paragraph","children":[{"type":"text","text":"€"}]}""";
        var atTheLimit = Prefix + new string(' ', MessageBlocks.MaxJsonBytes - Encoding.UTF8.GetByteCount(Prefix) - 1) + "]";
        var oneTooMany = Prefix + new string(' ', MessageBlocks.MaxJsonBytes - Encoding.UTF8.GetByteCount(Prefix)) + "]";

        Parse(atTheLimit).Error.Should().BeNull();
        Parse(oneTooMany).Error!.Reason.Should().Be("blocks.size");
    }

    [Fact]
    public void Parse_WithMentionsOfAccounts_ListsEachAccountOnce()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();

        var (error, parsed) = Parse($$"""
            [{"type":"paragraph","children":[{"type":"mention","accountId":"{{first}}"},{"type":"text","text":" and "},{"type":"mention","accountId":"{{second}}"}]},
             {"type":"code_block","children":[{"type":"mention","accountId":"{{first}}"}]}]
            """);

        error.Should().BeNull();
        parsed!.MentionedAccountIds.Should().Equal(first, second);
        JsonNode.Parse(MessageBlocks.ToJson(parsed.Blocks))![0]!["children"]![0]!.ToJsonString().Should().Be($$"""{"type":"mention","accountId":"{{first}}"}""");
    }

    [Fact]
    public void Parse_WithMoreThanFiftyMentions_IsRefused()
    {
        string Mentions(int count) => "[{\"type\":\"paragraph\",\"children\":[" + string.Join(',', Enumerable.Range(0, count).Select(_ => $$"""{"type":"mention","accountId":"{{Guid.CreateVersion7()}}"}""")) + "]}]";

        Parse(Mentions(MessageBlocks.MaxMentions)).Error.Should().BeNull();
        Parse(Mentions(MessageBlocks.MaxMentions + 1)).Error!.Code.Should().Be("messaging.mention_limit");
    }

    [Theory]
    [InlineData("""{"type":"mention","special":"everyone"}""")]
    [InlineData("""{"type":"mention","special":"here"}""")]
    [InlineData("""{"type":"mention","groupId":"0198a4c2-0000-7000-8000-000000000001"}""")]
    public void Parse_WithAGroupMention_IsRefusedUntilGroupsExist(string mention)
    {
        var (error, parsed) = Parse($$"""[{"type":"paragraph","children":[{"type":"text","text":"hi "},{{mention}}]}]""");

        parsed.Should().BeNull();
        error!.Code.Should().Be("messaging.mention_invalid");
    }

    [Fact]
    public void DeriveText_PutsEveryBlockAndListItemOnItsOwnLineWithoutMarkersOrAddresses()
    {
        var account = Guid.CreateVersion7();
        var (_, parsed) = Parse($$"""
            [{"type":"paragraph","children":[{"type":"text","text":"Hello "},{"type":"mention","accountId":"{{account}}"},{"type":"text","text":", see "},{"type":"text","text":"this","marks":[{"type":"link","href":"https://example.test/"}]}]},
             {"type":"paragraph","children":[]},
             {"type":"quote","children":[{"type":"text","text":"quoted\nline"}]},
             {"type":"bulleted_list","children":[{"type":"list_item","children":[{"type":"text","text":"one"}]},{"type":"list_item","children":[{"type":"text","text":"two"}]}]},
             {"type":"code_block","children":[{"type":"text","text":"code  "}]}]
            """);

        var text = MessageBlocks.DeriveText(parsed!, new Dictionary<Guid, string> { [account] = "Bob Stone" });

        text.Should().Be("Hello @Bob Stone, see this\n\nquoted\nline\none\ntwo\ncode  ");
    }

    [Theory]
    [InlineData("https://example.test/", "example.test")]
    [InlineData("http://example.test:8080/path?q=1#top", "example.test")]
    [InlineData("https://xn--mnchen-3ya.example/", "xn--mnchen-3ya.example")]
    [InlineData("mailto:someone@example.test", null)]
    public void TryReadHref_WithASafeAddress_ReturnsTheHostOfWebAddresses(string href, string? host)
    {
        MessageBlocks.TryReadHref(href, out var found).Should().BeTrue();

        found.Should().Be(host);
    }

    [Theory]
    [InlineData("")]
    [InlineData("example.test")]
    [InlineData("/relative")]
    [InlineData("//example.test/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("java\tscript:alert(1)")]
    [InlineData(" https://example.test/")]
    [InlineData("https://exa mple.test/")]
    [InlineData("HTTPS://example.test/")]
    [InlineData("data:text/html,x")]
    [InlineData("vbscript:x")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.test/")]
    [InlineData("https://")]
    [InlineData("mailto:")]
    [InlineData("https://münchen.example/")]
    public void TryReadHref_WithAnythingElse_IsRefused(string href)
    {
        MessageBlocks.TryReadHref(href, out _).Should().BeFalse();
    }

    [Fact]
    public void TryReadHref_WithOneCharacterTooMany_IsRefused()
    {
        const string Prefix = "https://example.test/";

        MessageBlocks.TryReadHref(Prefix + new string('a', MessageBlocks.MaxHrefLength - Prefix.Length), out _).Should().BeTrue();
        MessageBlocks.TryReadHref(Prefix + new string('a', MessageBlocks.MaxHrefLength - Prefix.Length + 1), out _).Should().BeFalse();
    }

    [Fact]
    public void ToLive_SerializedAsMessagePack_HasTheShapeOfTheJson()
    {
        var account = Guid.CreateVersion7();
        var (_, parsed) = Parse(Everything.TrimEnd()[..^1] + $$""",{"type":"paragraph","children":[{"type":"mention","accountId":"{{account}}"}]}]""");

        var packed = MessagePackSerializer.Serialize(MessageBlocks.ToLive(parsed!.Blocks), cancellationToken: TestContext.Current.CancellationToken);

        JsonNode.DeepEquals(JsonNode.Parse(MessagePackSerializer.ConvertToJson(packed, cancellationToken: TestContext.Current.CancellationToken)), JsonNode.Parse(MessageBlocks.ToJson(parsed.Blocks)))
            .Should().BeTrue();
    }

    [Fact]
    public void FromPlainText_IsOneParagraphWithOneTextNode()
    {
        MessageBlocks.ToJson(MessageBlocks.FromPlainText("hi").Blocks).Should().Be("""[{"type":"paragraph","children":[{"type":"text","text":"hi"}]}]""");
    }

    private static (Error? Error, ParsedBlocks? Blocks) Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var error = MessageBlocks.Parse(document.RootElement, out var parsed);
        return (error, parsed);
    }
}
