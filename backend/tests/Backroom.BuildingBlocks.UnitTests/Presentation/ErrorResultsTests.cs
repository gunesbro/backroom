using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Net.Http.Headers;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.BuildingBlocks.Presentation.Errors;

namespace Backroom.BuildingBlocks.UnitTests.Presentation;

public sealed class ErrorResultsTests
{
    [Theory]
    [InlineData(ErrorKind.Validation, StatusCodes.Status400BadRequest)]
    [InlineData(ErrorKind.Forbidden, StatusCodes.Status403Forbidden)]
    [InlineData(ErrorKind.NotFound, StatusCodes.Status404NotFound)]
    [InlineData(ErrorKind.Conflict, StatusCodes.Status409Conflict)]
    [InlineData(ErrorKind.RateLimited, StatusCodes.Status429TooManyRequests)]
    [InlineData(ErrorKind.Unexpected, StatusCodes.Status500InternalServerError)]
    public void ToStatusCode_ForEachKind_ReturnsStatus(ErrorKind kind, int expected)
    {
        kind.ToStatusCode().Should().Be(expected);
    }

    [Fact]
    public void ToStatusCode_WhenKindIsUnknown_Throws()
    {
        var map = () => ((ErrorKind)99).ToStatusCode();

        map.Should().Throw<UnreachableException>();
    }

    [Fact]
    public void ToProblem_WhenCalled_CarriesStableCode()
    {
        var problem = Error.Conflict("organization.already_member", "Already a member.").ToProblem()
            .Should().BeOfType<ProblemHttpResult>().Subject;

        problem.StatusCode.Should().Be(StatusCodes.Status409Conflict);
        problem.ProblemDetails.Extensions[ErrorResults.CodeExtension].Should().Be("organization.already_member");
        problem.ProblemDetails.Detail.Should().Be("Already a member.");
    }

    [Fact]
    public void ToProblem_WhenUnexpected_HidesTheDescription()
    {
        var problem = Error.Unexpected("messaging.store_failed", "Internal detail.").ToProblem()
            .Should().BeOfType<ProblemHttpResult>().Subject;

        problem.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
        problem.ProblemDetails.Detail.Should().BeNull();
        problem.ProblemDetails.Extensions[ErrorResults.CodeExtension].Should().Be("messaging.store_failed");
    }

    [Fact]
    public async Task ToProblem_WhenRateLimited_SetsRetryAfterInWholeSecondsAsync()
    {
        var error = Error.RateLimited("directory.too_many_attempts", "Try again later.", TimeSpan.FromMilliseconds(1500));
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Response.Body = new MemoryStream();

        await error.ToProblem().ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        context.Response.Headers[HeaderNames.RetryAfter].ToString().Should().Be("2");
        context.Response.ContentType.Should().StartWith("application/problem+json");
    }

    [Fact]
    public void ToHttpResult_WhenSuccess_UsesTheSuccessResult()
    {
        Result<int> result = 7;

        result.ToHttpResult(TypedResults.Ok).Should().BeOfType<Ok<int>>();
    }

    [Fact]
    public void ToHttpResult_WhenSuccessWithoutValue_ReturnsNoContent()
    {
        Result.Success.ToHttpResult().Should().BeOfType<NoContent>();
    }

    [Fact]
    public void ToHttpResult_WhenFailure_ReturnsProblem()
    {
        Result result = Error.Forbidden("permissions.denied", "Missing permission.");

        result.ToHttpResult().Should().BeOfType<ProblemHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public void ToHttpResult_WhenUninitialized_ReturnsServerError()
    {
        default(Result).ToHttpResult().Should().BeOfType<ProblemHttpResult>()
            .Which.StatusCode.Should().Be(StatusCodes.Status500InternalServerError);
    }
}
