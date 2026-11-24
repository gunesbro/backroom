using System.Collections.Concurrent;
using System.Diagnostics;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;

namespace Backroom.ServiceDefaults.IntegrationTests;

/// <summary>A SignalR WebSocket carries its access token in the query string; it must never reach a trace.</summary>
public sealed class QueryRedactionTests
{
    [Theory]
    [InlineData("", "")]
    [InlineData("?id=1", "?id=1")]
    [InlineData("?access_token=abc.def.ghi", "?access_token=[REDACTED]")]
    [InlineData("?id=7&access_token=abc&x=y", "?id=7&access_token=[REDACTED]&x=y")]
    [InlineData("access_token=abc", "access_token=[REDACTED]")]
    [InlineData("?ACCESS_TOKEN=abc", "?ACCESS_TOKEN=[REDACTED]")]
    [InlineData("?access%5Ftoken=abc", "?access%5Ftoken=[REDACTED]")]
    [InlineData("?access_token", "?access_token=[REDACTED]")]
    [InlineData("?my_access_token=keep", "?my_access_token=keep")]
    public void Redact_WithAQueryString_HidesTheTokenValueOnly(string query, string expected) =>
        QueryRedaction.Redact(query).Should().Be(expected);

    [Fact]
    public async Task Trace_OfARequestWithATokenInTheQuery_DoesNotCarryTheTokenAsync()
    {
        var queries = new ConcurrentBag<string>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == "Microsoft.AspNetCore",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity =>
            {
                if (activity.GetTagItem("url.query") is string query)
                {
                    queries.Add(query);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = Environments.Production });
        builder.WebHost.UseTestServer();
        builder.AddServiceDefaults();
        await using var app = builder.Build();
        app.MapGet("/hub", () => "ok");
        await app.StartAsync(TestContext.Current.CancellationToken);
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(new Uri("/hub?id=1&access_token=secret-token-value", UriKind.Relative), TestContext.Current.CancellationToken);

        response.EnsureSuccessStatusCode();
        queries.Should().NotBeEmpty();
        queries.Should().OnlyContain(query => !query.Contains("secret-token-value", StringComparison.Ordinal));
        queries.Should().Contain(query => query.Contains("access_token=[REDACTED]", StringComparison.Ordinal));
    }
}
