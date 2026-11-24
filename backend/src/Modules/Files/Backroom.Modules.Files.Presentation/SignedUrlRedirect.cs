using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace Backroom.Modules.Files.Presentation;

/// <summary>
/// <c>302</c> to a signed storage URL. Not <c>TypedResults.Redirect</c>: that logs its destination, and the signature
/// in it must never reach a log ("Privacy rules": the signed URL is never cached or logged). No cache may keep the
/// answer, and the URL is not sent on as a referrer.
/// </summary>
internal sealed class SignedUrlRedirect(Uri url) : IResult, IStatusCodeHttpResult
{
    public int? StatusCode => StatusCodes.Status302Found;

    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        var response = httpContext.Response;
        response.StatusCode = StatusCodes.Status302Found;
        response.Headers.Location = url.AbsoluteUri;
        response.Headers.CacheControl = "no-store";
        response.Headers[HeaderNames.XContentTypeOptions] = "nosniff";
        response.Headers["Referrer-Policy"] = "no-referrer";
        return Task.CompletedTask;
    }
}
