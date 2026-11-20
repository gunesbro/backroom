using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Backroom.BuildingBlocks.Application.Persistence;

namespace Backroom.BuildingBlocks.Presentation.Persistence;

public static class UnitOfWorkEndpointExtensions
{
    /// <summary>
    /// Runs each endpoint in a unit of work: begins it before the handler and commits it when the
    /// result is not an error (status below 400). Error results and exceptions roll it back. Add it
    /// after the filter that sets the organization context, so the transaction is scoped to it.
    /// Handlers return materialized results: a lazy query or stream would be read after the commit
    /// and fail.
    /// </summary>
    public static TBuilder WithUnitOfWork<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.AddEndpointFilter(static async (context, next) =>
        {
            var httpContext = context.HttpContext;
            var unitOfWork = httpContext.RequestServices.GetRequiredService<IUnitOfWork>();

            await unitOfWork.BeginAsync(httpContext.RequestAborted);
            object? result;
            try
            {
                result = await next(context);
            }
            catch
            {
                // Release the connection now rather than after the exception handler has run.
                await unitOfWork.RollbackAsync(CancellationToken.None);
                throw;
            }

            if (IsError(result, httpContext))
            {
                await unitOfWork.RollbackAsync(CancellationToken.None);
            }
            else
            {
                // Not cancelled by a client disconnect: the work is done, and a retry should find it committed.
                await unitOfWork.CommitAsync(CancellationToken.None);
            }

            return result;
        });
    }

    private static bool IsError(object? result, HttpContext httpContext)
    {
        // Typed unions such as Results<Created, ProblemHttpResult> wrap the actual result.
        while (result is INestedHttpResult nested)
        {
            result = nested.Result;
        }

        return result is IStatusCodeHttpResult { StatusCode: { } status }
            ? status >= StatusCodes.Status400BadRequest
            : httpContext.Response.StatusCode >= StatusCodes.Status400BadRequest;
    }
}
