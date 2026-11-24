using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;

namespace Backroom.BuildingBlocks.Presentation.Cors;

public static class WebOriginsCorsExtensions
{
    /// <summary>The name of the policy.</summary>
    public const string PolicyName = "web-origins";

    /// <summary>How long browsers may cache a preflight answer.</summary>
    public static readonly TimeSpan PreflightMaxAge = TimeSpan.FromSeconds(600);

    private static readonly string[] s_methods = ["GET", "POST", "DELETE"];

    // Authorization and Content-Type for the API; the two SignalR headers for the hub's negotiate request.
    private static readonly string[] s_requestHeaders = ["Authorization", "Content-Type", "X-Requested-With", "x-signalr-user-agent"];

    private static readonly string[] s_exposedHeaders = ["Retry-After"];

    /// <summary>
    /// Registers the CORS policy built from <c>Cors:AllowedOrigins</c> (validated at startup). APIs take bearer tokens only,
    /// so credentials are never allowed and CSRF does not apply. Pair with <see cref="UseWebOriginsCors"/>.
    /// </summary>
    public static IHostApplicationBuilder AddWebOriginsCors(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        var development = builder.Environment.IsDevelopment();

        builder.Services.AddOptions<WebOriginsOptions>()
            .BindConfiguration(WebOriginsOptions.SectionName)
            .Validate(
                options => options.AllowedOrigins.TrueForAll(origin => WebOriginRules.IsValid(origin, development)),
                "Cors:AllowedOrigins must list exact origins (scheme, host and port; no wildcards, no paths); "
                + "https is required, except http on localhost, 127.0.0.1 and [::1] in Development and http://tauri.localhost; also tauri://localhost.")
            .ValidateOnStart();

        builder.Services.AddCors();
        builder.Services.AddOptions<CorsOptions>()
            .Configure<IOptions<WebOriginsOptions>>((cors, web) => cors.AddPolicy(PolicyName, policy =>
            {
                // No origins: the policy matches nothing, so no CORS header is ever sent.
                policy.WithOrigins([.. web.Value.AllowedOrigins])
                    .WithMethods(s_methods)
                    .WithHeaders(s_requestHeaders)
                    .WithExposedHeaders(s_exposedHeaders)
                    .SetPreflightMaxAge(PreflightMaxAge)
                    .DisallowCredentials();
            }));
        return builder;
    }

    /// <summary>Adds the CORS middleware. Place it before authentication, so a preflight (which carries no token) is answered, not refused.</summary>
    public static IApplicationBuilder UseWebOriginsCors(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        // The answer depends on the Origin header, also when one origin is configured or the origin is refused; caches must know.
        app.Use(async (context, next) =>
        {
            context.Response.OnStarting(static state =>
            {
                ((HttpContext)state).Response.Headers.Append(HeaderNames.Vary, HeaderNames.Origin);
                return Task.CompletedTask;
            }, context);
            await next(context);
        });
        return app.UseCors(PolicyName);
    }
}
