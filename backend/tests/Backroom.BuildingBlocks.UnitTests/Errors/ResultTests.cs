using AwesomeAssertions;
using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.BuildingBlocks.UnitTests.Errors;

public sealed class ResultTests
{
    private static readonly Error s_notFound = Error.NotFound("messaging.channel_not_found", "Channel not found.");

    [Fact]
    public void Success_WhenCreated_HasNoError()
    {
        var result = Result.Success;

        result.IsSuccess.Should().BeTrue();
        result.Error.Should().BeNull();
    }

    [Fact]
    public void ImplicitConversion_FromError_IsFailure()
    {
        Result result = s_notFound;

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(s_notFound);
    }

    [Fact]
    public void Default_WhenUninitialized_IsFailure()
    {
        var result = default(Result);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Result.Uninitialized);
        result.Error!.Code.Should().Be("result.uninitialized");
    }

    [Fact]
    public void Failure_WhenErrorIsNull_Throws()
    {
        var create = () => Result.Failure(null!);

        create.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void ToString_WhenCalled_ShowsOnlyTheOutcomeAndCode()
    {
        Result failure = s_notFound;

        failure.ToString().Should().Be("Failure(messaging.channel_not_found)");
        Result.Success.ToString().Should().Be("Success");
    }

    [Fact]
    public void GenericResult_FromValue_ExposesValue()
    {
        Result<int> result = 42;

        result.IsSuccess.Should().BeTrue();
        result.Error.Should().BeNull();
        result.GetValueOrThrow().Should().Be(42);
    }

    [Fact]
    public void GenericResult_FromError_ThrowsOnValue()
    {
        Result<int> result = s_notFound;

        var read = () => result.GetValueOrThrow();

        result.IsSuccess.Should().BeFalse();
        read.Should().Throw<InvalidOperationException>().WithMessage("*messaging.channel_not_found*");
    }

    [Fact]
    public void GenericResult_WhenUninitialized_IsFailure()
    {
        var result = default(Result<string>);

        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Be(Result.Uninitialized);
    }

    [Fact]
    public void GenericResult_FailureWithNullError_Throws()
    {
        var create = () => Result<int>.Failure(null!);

        create.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void GenericResult_ToString_DoesNotThrow()
    {
        Result<int> failure = s_notFound;
        Result<int> success = 7;

        failure.ToString().Should().Be("Failure(messaging.channel_not_found)");
        success.ToString().Should().Be("Success(7)");
        default(Result<int>).ToString().Should().Be("Failure(result.uninitialized)");
    }

    [Fact]
    public void Match_WhenSuccess_CallsOnSuccess()
    {
        Result<int> result = 3;

        result.Match(value => $"value {value}", error => error.Code).Should().Be("value 3");
    }

    [Fact]
    public void Match_WhenFailure_CallsOnFailure()
    {
        Result<int> result = s_notFound;

        result.Match(value => $"value {value}", error => error.Code).Should().Be("messaging.channel_not_found");
    }
}
