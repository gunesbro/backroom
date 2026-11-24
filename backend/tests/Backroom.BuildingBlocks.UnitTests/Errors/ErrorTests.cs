using AwesomeAssertions;
using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.BuildingBlocks.UnitTests.Errors;

public sealed class ErrorTests
{
    [Theory]
    [InlineData("messaging.channel_not_found")]
    [InlineData("directory.invitation_invalid")]
    [InlineData("organization.v2.member_deactivated")]
    public void Create_WhenCodeIsModuleAndSnakeCase_Succeeds(string code)
    {
        Error.NotFound(code, "description").Code.Should().Be(code);
    }

    [Theory]
    [InlineData("")]
    [InlineData("channel_not_found")]
    [InlineData("Messaging.ChannelNotFound")]
    [InlineData("messaging.channel-not-found")]
    [InlineData("messaging.")]
    [InlineData("MESSAGİNG.not_found")]
    public void Create_WhenCodeIsMalformed_Throws(string code)
    {
        var create = () => Error.Validation(code, "description");

        create.Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_WhenDescriptionIsBlank_Throws(string description)
    {
        var create = () => Error.Validation("messaging.invalid", description);

        create.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Factories_WhenCalled_SetTheKind()
    {
        Error.Validation("m.a", "d").Kind.Should().Be(ErrorKind.Validation);
        Error.NotFound("m.a", "d").Kind.Should().Be(ErrorKind.NotFound);
        Error.Conflict("m.a", "d").Kind.Should().Be(ErrorKind.Conflict);
        Error.Forbidden("m.a", "d").Kind.Should().Be(ErrorKind.Forbidden);
        Error.RateLimited("m.a", "d", TimeSpan.FromSeconds(1)).Kind.Should().Be(ErrorKind.RateLimited);
        Error.ContentTooLarge("m.a", "d").Kind.Should().Be(ErrorKind.ContentTooLarge);
        Error.Unexpected("m.a", "d").Kind.Should().Be(ErrorKind.Unexpected);
    }

    [Fact]
    public void RateLimited_WhenCreated_CarriesRetryAfter()
    {
        Error.RateLimited("directory.too_many_attempts", "Try again later.", TimeSpan.FromSeconds(30))
            .RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RateLimited_WhenRetryAfterIsNotPositive_Throws(int seconds)
    {
        var create = () => Error.RateLimited("m.a", "d", TimeSpan.FromSeconds(seconds));

        create.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void ErrorKind_WhenUninitialized_IsUnexpected()
    {
        default(ErrorKind).Should().Be(ErrorKind.Unexpected);
    }
}
