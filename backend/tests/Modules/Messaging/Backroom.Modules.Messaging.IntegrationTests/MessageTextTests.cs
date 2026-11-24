using AwesomeAssertions;
using Backroom.Modules.Messaging.Application.Messages;

namespace Backroom.Modules.Messaging.IntegrationTests;

public sealed class MessageTextTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("  padded \n", "padded")]
    [InlineData("<a href=\"x\">&'</a>", "&lt;a href=&quot;x&quot;&gt;&amp;&#39;&lt;/a&gt;")]
    [InlineData("one\r\ntwo\tthree", "one\ntwo\tthree")]
    [InlineData("Ünïcödé ✓ 👍", "Ünïcödé ✓ 👍")]
    public void TryPrepare_WithAValidText_TrimsAndEscapes(string raw, string expected)
    {
        MessageText.TryPrepare(raw, out var stored).Should().BeTrue();

        stored.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \r\n\t ")]
    [InlineData("bell\a")]
    [InlineData("nul\0")]
    public void TryPrepare_WithAnInvalidText_IsRefused(string? raw)
    {
        MessageText.TryPrepare(raw, out _).Should().BeFalse();
    }

    [Fact]
    public void TryPrepare_WithALoneSurrogate_IsRefused()
    {
        MessageText.TryPrepare("a" + (char)0xDC00, out _).Should().BeFalse();
        MessageText.TryPrepare("a" + (char)0xD800, out _).Should().BeFalse();
    }

    [Fact]
    public void TryPrepare_AtTheLimit_IsAcceptedAndOneMoreIsNot()
    {
        MessageText.TryPrepare(new string('a', MessageText.MaxLength), out _).Should().BeTrue();
        MessageText.TryPrepare(new string('a', MessageText.MaxLength + 1), out _).Should().BeFalse();
        MessageText.TryPrepare(new string('"', MessageText.MaxLength), out var stored).Should().BeTrue();
        stored!.Length.Should().Be(MessageText.MaxStoredLength);
    }

    [Fact]
    public void ToBlocks_ReturnsOneParagraphWithTheText()
    {
        var blocks = System.Text.Json.Nodes.JsonNode.Parse(MessageText.ToBlocks("a &lt; b"))!.AsArray();

        blocks.Should().ContainSingle();
        blocks[0]!["type"]!.GetValue<string>().Should().Be("paragraph");
        blocks[0]!["text"]!.GetValue<string>().Should().Be("a &lt; b");
    }
}
