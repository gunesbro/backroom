using System.Globalization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Net.Http.Headers;
using Npgsql;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Epoch;

/// <summary>
/// Write fencing of the app role (docs/adr/0011, "Cell epoch"; docs/contracts/phase-2.md, "Cell epoch"): every
/// state-changing request, that is every method other than GET, HEAD, OPTIONS and TRACE, is refused with
/// <c>503 cell.epoch_unconfirmed</c> and <c>Retry-After</c> while the cell's stored epoch is not confirmed. Reads are
/// never fenced. Exempt paths (the live-event hub, whose negotiate is a POST that changes nothing) are given by the
/// host. Runs after authorization, so a caller without a valid token still gets 401 and learns nothing.
/// </summary>
public static class CellEpochFence
{
    /// <summary>The stable error code of a fenced write.</summary>
    public const string Code = "cell.epoch_unconfirmed";

    /// <summary>The <c>Retry-After</c> of a fenced write: reconnecting takes a few seconds.</summary>
    internal const int RetryAfterSeconds = 5;

    /// <summary>The app role's fence; needs the database session (<c>AddDatabaseSession</c>) and <c>Cell:Id</c>.</summary>
    public static IServiceCollection AddCellEpochFence(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton(provider => new CellEpochGate(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<IConfiguration>()["Cell:Id"] is { Length: > 0 } cellId
                ? cellId
                : throw new InvalidOperationException("Cell:Id is required for the epoch fence."),
            provider.GetRequiredService<TimeProvider>()));
        return services;
    }

    /// <summary>Refuses writes while the epoch is not confirmed; <paramref name="exemptPaths"/> are never fenced.</summary>
    public static IApplicationBuilder UseCellEpochFence(this IApplicationBuilder app, params string[] exemptPaths)
    {
        ArgumentNullException.ThrowIfNull(app);
        ArgumentNullException.ThrowIfNull(exemptPaths);
        var exempt = exemptPaths.Select(path => new PathString(path)).ToArray();
        var gate = app.ApplicationServices.GetRequiredService<CellEpochGate>();
        return app.Use(async (context, next) =>
        {
            if (IsWrite(context.Request.Method)
                && !Array.Exists(exempt, path => context.Request.Path.StartsWithSegments(path, StringComparison.OrdinalIgnoreCase))
                && await gate.ConfirmedEpochAsync(context.RequestAborted) is null)
            {
                await RefuseAsync(context);
                return;
            }

            await next(context);
        });
    }

    internal static bool IsWrite(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method) && !HttpMethods.IsTrace(method);

    private static Task RefuseAsync(HttpContext context)
    {
        context.Response.Headers[HeaderNames.RetryAfter] = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        context.Response.Headers.CacheControl = "no-store";
        return TypedResults.Problem(
            statusCode: StatusCodes.Status503ServiceUnavailable,
            detail: "The cell has not confirmed its epoch with the control plane yet; writes are refused until it has.",
            extensions: new Dictionary<string, object?>(StringComparer.Ordinal) { ["code"] = Code }).ExecuteAsync(context);
    }
}
