using System.Diagnostics;
using System.Globalization;
using Microsoft.AspNetCore.Http;

namespace Backroom.ServiceDefaults;

/// <summary>
/// Makes every problem response carry a stable <c>code</c> (contract: "Errors") and the trace id,
/// including those produced by the exception handler and status code pages.
/// </summary>
public static class ProblemDetailsDefaults
{
    public const string CodeExtension = "code";
    public const string TraceIdExtension = "traceId";

    public static void Customize(ProblemDetailsContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var extensions = context.ProblemDetails.Extensions;

        extensions[TraceIdExtension] = Activity.Current?.TraceId.ToString() ?? context.HttpContext.TraceIdentifier;

        if (!extensions.ContainsKey(CodeExtension))
        {
            extensions[CodeExtension] = CodeFor(context.ProblemDetails.Status ?? context.HttpContext.Response.StatusCode);
        }
    }

    /// <summary>Code for problems that did not come from a domain error.</summary>
    public static string CodeFor(int statusCode) => statusCode switch
    {
        StatusCodes.Status400BadRequest => "http.bad_request",
        StatusCodes.Status401Unauthorized => "http.unauthorized",
        StatusCodes.Status403Forbidden => "http.forbidden",
        StatusCodes.Status404NotFound => "http.not_found",
        StatusCodes.Status405MethodNotAllowed => "http.method_not_allowed",
        StatusCodes.Status413PayloadTooLarge => "http.payload_too_large",
        StatusCodes.Status415UnsupportedMediaType => "http.unsupported_media_type",
        StatusCodes.Status429TooManyRequests => "http.too_many_requests",
        >= 500 => "server.unexpected",
        _ => "http.status_" + statusCode.ToString(CultureInfo.InvariantCulture),
    };
}
