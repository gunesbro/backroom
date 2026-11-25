using AwesomeAssertions;
using Backroom.Modules.Messaging.Application.Channels;
using Backroom.Modules.Messaging.Application.Conversations;
using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>The input rules of channel management, without a database.</summary>
public sealed class ChannelInputTests
{
    [Theory]
    [InlineData(null, null)]
    [InlineData("", null)]
    [InlineData("  \t ", null)]
    [InlineData("  What we do  ", "What we do")]
    [InlineData("İstanbul ofisi", "İstanbul ofisi")]
    public void Description_WhenValid_IsTrimmedOrNothing(string? raw, string? expected)
    {
        ChannelDescription.TryNormalize(raw, out var description).Should().BeTrue();

        description.Should().Be(expected);
    }

    [Theory]
    [InlineData("line\nbreak")]
    [InlineData("nul\0")]
    public void Description_WithControlCharacters_IsRefused(string raw) =>
        ChannelDescription.TryNormalize(raw, out _).Should().BeFalse();

    [Fact]
    public void Description_IsBoundedAt250Characters()
    {
        ChannelDescription.TryNormalize(new string('a', ChannelDescription.MaxLength), out _).Should().BeTrue();
        ChannelDescription.TryNormalize(new string('a', ChannelDescription.MaxLength + 1), out _).Should().BeFalse();
    }

    [Fact]
    public void AccountIds_AreDistinctSortedAndWithoutTheExcludedOne()
    {
        var low = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var high = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var self = Guid.Parse("00000000-0000-0000-0000-000000000003");

        AccountIdList.TryDistinct([high, self, low, high], 4, self, out var ids).Should().BeTrue();

        ids.Should().Equal(low, high);
    }

    [Fact]
    public void AccountIds_WhenTooManyOrEmptyId_AreRefusedAndNoListIsEmpty()
    {
        AccountIdList.TryDistinct([Guid.NewGuid(), Guid.NewGuid()], 1, null, out _).Should().BeFalse();
        AccountIdList.TryDistinct([Guid.Empty], 5, null, out _).Should().BeFalse();
        AccountIdList.TryDistinct(null, 5, null, out var none).Should().BeTrue();
        none.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ConversationKinds.PublicChannel, null, true)]
    [InlineData(ConversationKinds.PrivateChannel, null, false)]
    [InlineData(ConversationKinds.PrivateChannel, "member", true)]
    [InlineData(ConversationKinds.GroupDm, null, false)]
    [InlineData(ConversationKinds.Dm, "member", true)]
    public void Conversation_IsVisibleOnlyWhenPublicOrToItsMembers(string kind, string? role, bool expected) =>
        new ConversationDetails(Guid.NewGuid(), kind, null, null, DateTimeOffset.UnixEpoch, null, 0, 1, null, role).IsVisible.Should().Be(expected);
}
