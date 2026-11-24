using AwesomeAssertions;
using Backroom.Modules.Messaging.Application.Messages;

namespace Backroom.Modules.Messaging.IntegrationTests;

public sealed class MessageTextTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("  padded \n", "padded")]
    [InlineData("<a href=\"x\">&'</a>", "<a href=\"x\">&'</a>")]
    [InlineData("one\r\ntwo\rthree\tfour", "one\ntwo\nthree\tfour")]
    [InlineData("Ünïcödé ✓ 👍", "Ünïcödé ✓ 👍")]
    public void TryPrepare_WithAValidText_TrimsAndKeepsItRaw(string raw, string expected)
    {
        MessageText.TryPrepare(raw, out var text).Should().BeTrue();

        text.Should().Be(expected);
    }

    [Fact]
    public void TryPrepare_NormalizesToNfc()
    {
        MessageText.TryPrepare("cafe" + (char)0x301, out var text).Should().BeTrue();

        text.Should().Be("café");
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
        MessageText.TryPrepare(new string('"', MessageText.MaxLength), out var text).Should().BeTrue();
        text!.Length.Should().Be(MessageText.MaxLength);
    }

    [Fact]
    public void ToBlocks_ReturnsOneParagraphWithOneTextNodeHoldingThePlainText()
    {
        var blocks = System.Text.Json.Nodes.JsonNode.Parse(MessageText.ToBlocks("a < b & c"))!.AsArray();

        blocks.Should().ContainSingle();
        blocks[0]!["type"]!.GetValue<string>().Should().Be("paragraph");
        var children = blocks[0]!["children"]!.AsArray();
        children.Should().ContainSingle();
        children[0]!["type"]!.GetValue<string>().Should().Be("text");
        children[0]!["text"]!.GetValue<string>().Should().Be("a < b & c");
    }
}
