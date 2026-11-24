using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

namespace Backroom.ServiceDefaults;

/// <summary>
/// Common host setup shared by every service: OpenTelemetry, health checks,
/// service discovery and HTTP resilience.
/// </summary>
public static class ServiceDefaultsExtensions
{
    public const string HealthEndpointPath = "/health";
    public const string AlivenessEndpointPath = "/alive";

    public static TBuilder AddServiceDefaults<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.ConfigureOpenTelemetry();
        builder.AddDefaultHealthChecks();

        // RFC 9457 problem details for every error response, with a stable code and the trace id.
        builder.Services.AddProblemDetails(options => options.CustomizeProblemDetails = ProblemDetailsDefaults.Customize);

        builder.Services.AddServiceDiscovery();
        builder.Services.ConfigureHttpClientDefaults(http =>
        {
            http.AddStandardResilienceHandler();
            http.AddServiceDiscovery();
        });

        return builder;
    }

    public static TBuilder ConfigureOpenTelemetry<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Logging.AddOpenTelemetry(logging =>
        {
            logging.IncludeFormattedMessage = true;
            logging.IncludeScopes = true;
        });

        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                // Maintenance job rows, failed runs, backlog and oldest due age (Backroom.BuildingBlocks.Infrastructure.Maintenance).
                .AddMeter("Backroom.Maintenance")
                // Hub connections, subscriptions and live event publishes (Backroom.Modules.Realtime.Infrastructure).
                .AddMeter("Backroom.Realtime"))
            .WithTracing(tracing => tracing
                .AddSource(builder.Environment.ApplicationName)
                // Npgsql's built-in activity source: one span per command (SQL text, no parameter values).
                .AddSource("Npgsql")
                // Outbox publish and event consume spans (Backroom.BuildingBlocks.Infrastructure.Messaging).
                .AddSource("Backroom.Messaging")
                // Maintenance job runs (Backroom.BuildingBlocks.Infrastructure.Maintenance).
                .AddSource("Backroom.Maintenance")
                // Settling cell link messages (Backroom.Modules.CellManagement.Application.Link).
                .AddSource("Backroom.CellLink")
                // Email sends with their delivery ids (Backroom.Modules.Email.Application.Deliveries).
                .AddSource("Backroom.Email")
                // The control plane's invitation expiry and retention runs (Backroom.Modules.Directory.Application.Invitations).
                .AddSource("Backroom.Directory")
                .AddAspNetCoreInstrumentation(options =>
                {
                    // A SignalR WebSocket carries its access token in the query string: never into a trace.
                    options.EnrichWithHttpRequest = static (activity, request) =>
                    {
                        if (request.QueryString.HasValue)
                        {
                            activity.SetTag("url.query", QueryRedaction.Redact(request.QueryString.Value!));
                        }
                    };

                    // Health probes would flood the traces.
                    options.Filter = context =>
                        !context.Request.Path.StartsWithSegments(HealthEndpointPath, StringComparison.OrdinalIgnoreCase)
                        && !context.Request.Path.StartsWithSegments(AlivenessEndpointPath, StringComparison.OrdinalIgnoreCase);
                })
                .AddHttpClientInstrumentation());

        if (!string.IsNullOrWhiteSpace(builder.Configuration["OTEL_EXPORTER_OTLP_ENDPOINT"]))
        {
            builder.Services.AddOpenTelemetry().UseOtlpExporter();
        }

        return builder;
    }

    public static TBuilder AddDefaultHealthChecks<TBuilder>(this TBuilder builder)
        where TBuilder : IHostApplicationBuilder
    {
        builder.Services.AddHealthChecks()
            .AddCheck("self", () => HealthCheckResult.Healthy(), ["live"]);

        return builder;
    }

    /// <summary>
    /// Unhandled exceptions and bare status codes become problem details. Exception details are
    /// never sent to clients; they are in the logs and traces.
    /// </summary>
    public static WebApplication UseDefaultErrorHandling(this WebApplication app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        return app;
    }

    /// <summary>
    /// Maps the health endpoints. Only in Development: exposing them publicly has
    /// security implications and is decided in the deploy phase.
    /// </summary>
    public static WebApplication MapDefaultEndpoints(this WebApplication app)
    {
        if (app.Environment.IsDevelopment())
        {
            app.MapHealthChecks(HealthEndpointPath).AllowAnonymous();
            app.MapHealthChecks(AlivenessEndpointPath, new HealthCheckOptions
            {
                Predicate = registration => registration.Tags.Contains("live"),
            }).AllowAnonymous();
        }

        return app;
    }
}
