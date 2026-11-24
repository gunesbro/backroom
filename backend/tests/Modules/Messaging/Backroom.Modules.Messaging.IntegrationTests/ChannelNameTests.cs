using AwesomeAssertions;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Testing;

namespace Backroom.Modules.Messaging.IntegrationTests;

public sealed class ChannelNameTests
{
    [Theory]
    [InlineData("  general  ", "general")]
    [InlineData("Team Alpha", "Team Alpha")]
    [InlineData("café", "café")]
    public void TryNormalize_WithAValidName_TrimsAndNormalizes(string raw, string expected)
    {
        ChannelName.TryNormalize(raw, out var name).Should().BeTrue();

        name.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("a\nb")]
    [InlineData("a\0b")]
    public void TryNormalize_WithAnInvalidName_IsRefused(string? raw)
    {
        ChannelName.TryNormalize(raw, out _).Should().BeFalse();
    }

    [Fact]
    public void TryNormalize_WithALoneSurrogate_IsRefused()
    {
        ChannelName.TryNormalize("a" + (char)0xD800 + "b", out _).Should().BeFalse();
    }

    [Fact]
    public void TryNormalize_AtTheLimit_IsAcceptedAndOneMoreIsNot()
    {
        ChannelName.TryNormalize(new string('a', ChannelName.MaxLength), out _).Should().BeTrue();
        ChannelName.TryNormalize(new string('a', ChannelName.MaxLength + 1), out _).Should().BeFalse();
    }

    [Fact]
    public void KeyOf_UnderTurkishCulture_StaysInvariant()
    {
        using var scope = CultureScope.Turkish();

        // Turkish rules would make "I" lower-case to a dotless i and keep these two apart.
        ChannelName.KeyOf("ISTANBUL").Should().Be(ChannelName.KeyOf("istanbul"));
        ChannelName.KeyOf("TITLE").Should().Be("title");
    }
}
