using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Messaging.Application.Live;

namespace Backroom.Modules.Messaging.Presentation;

internal static class LiveEventEndpointExtensions
{
    /// <summary>The most a request waits for the live events it caused; they are best effort.</summary>
    private static readonly TimeSpan s_publishTimeout = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Sends the live events the handler queued, after the unit of work has committed and only if the request
    /// succeeded. Add it <b>before</b> <c>InOrganization()</c>: filters run in the order added, so this one wraps the
    /// unit of work and sees its commit. A publish failure is logged by <see cref="AfterCommitLiveEvents"/> and
    /// never changes the response.
    /// </summary>
    public static TBuilder PublishLiveEventsAfterCommit<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(static async (context, next) =>
        {
            var result = await next(context);
            if (!IsError(result, context.HttpContext))
            {
                // Not cancelled by a client disconnect: the message is stored, and the others should still see it.
                using var timeout = new CancellationTokenSource(s_publishTimeout);
                await context.HttpContext.RequestServices.GetRequiredService<AfterCommitLiveEvents>().FlushAsync(timeout.Token);
            }

            return result;
        });
    }

    private static bool IsError(object? result, HttpContext httpContext)
    {
        // Typed unions wrap the actual result.
        while (result is INestedHttpResult nested)
        {
            result = nested.Result;
        }

        return result is IStatusCodeHttpResult { StatusCode: { } status }
            ? status >= StatusCodes.Status400BadRequest
            : httpContext.Response.StatusCode >= StatusCodes.Status400BadRequest;
    }
}
