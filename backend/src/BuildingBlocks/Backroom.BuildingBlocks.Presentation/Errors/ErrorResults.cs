using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.Net.Http.Headers;
using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.BuildingBlocks.Presentation.Errors;

/// <summary>
/// Turns expected failures into RFC 9457 problem details with a stable <c>code</c> extension.
/// Title and type come from the status code (RFC 9110), like every other problem response;
/// the trace id is added by the host's problem details service.
/// </summary>
public static class ErrorResults
{
    public const string CodeExtension = "code";

    /// <summary>The fixed token of <see cref="Error.Reason"/>; present only when the error has one.</summary>
    public const string ReasonExtension = "reason";

    public static int ToStatusCode(this ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.RateLimited => StatusCodes.Status429TooManyRequests,
        ErrorKind.ContentTooLarge => StatusCodes.Status413PayloadTooLarge,
        ErrorKind.Unexpected => StatusCodes.Status500InternalServerError,
        _ => throw new UnreachableException($"Unknown error kind {kind}."),
    };

    public static IResult ToProblem(this Error error)
    {
        ArgumentNullException.ThrowIfNull(error);

        var extensions = new Dictionary<string, object?>(StringComparer.Ordinal) { [CodeExtension] = error.Code };
        if (error.Reason is { } reason && error.Kind != ErrorKind.Unexpected)
        {
            extensions[ReasonExtension] = reason;
        }

        var problem = TypedResults.Problem(
            statusCode: error.Kind.ToStatusCode(),
            // Unexpected errors keep their description out of responses; it is in logs and traces.
            detail: error.Kind == ErrorKind.Unexpected ? null : error.Description,
            extensions: extensions);

        return error.RetryAfter is { } retryAfter ? new RetryAfterResult(problem, retryAfter) : problem;
    }

    /// <summary>The success value as <paramref name="onSuccess"/> builds it, or the error as problem details.</summary>
    public static IResult ToHttpResult<T>(this Result<T> result, Func<T, IResult> onSuccess)
    {
        ArgumentNullException.ThrowIfNull(onSuccess);
        return result.IsSuccess ? onSuccess(result.GetValueOrThrow()) : result.Error.ToProblem();
    }

    /// <summary><c>204 No Content</c> on success, or the error as problem details.</summary>
    public static IResult ToHttpResult(this Result result) =>
        result.IsSuccess ? TypedResults.NoContent() : result.Error.ToProblem();

    /// <summary>Adds <c>Retry-After</c> (whole seconds, rounded up) to a 429 problem.</summary>
    private sealed class RetryAfterResult(ProblemHttpResult problem, TimeSpan retryAfter) : IResult, IStatusCodeHttpResult
    {
        // Lets filters such as the unit of work see that this is an error.
        public int? StatusCode => problem.StatusCode;

        public Task ExecuteAsync(HttpContext httpContext)
        {
            ArgumentNullException.ThrowIfNull(httpContext);
            var seconds = (long)Math.Ceiling(retryAfter.TotalSeconds);
            httpContext.Response.Headers[HeaderNames.RetryAfter] = seconds.ToString(CultureInfo.InvariantCulture);
            return problem.ExecuteAsync(httpContext);
        }
    }
}
