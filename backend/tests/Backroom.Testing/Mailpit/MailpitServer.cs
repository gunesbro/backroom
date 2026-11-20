using System.Net.Http.Json;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Xunit;

namespace Backroom.Testing.Mailpit;

/// <summary>
/// A Mailpit server in a container for integration tests (docs/adr/0008, item 8): mail goes in over SMTP and
/// is read back through Mailpit's API. Relay is not configured. Share one instance per test assembly
/// (<c>[assembly: AssemblyFixture(typeof(MailpitServer))]</c>); tests use their own <c>.test</c> addresses, so they
/// do not need to clear it.
/// </summary>
public sealed class MailpitServer : IAsyncLifetime
{
    /// <summary>The same version as the AppHost's Mailpit (checked by <c>MailpitVersionTests</c>).</summary>
    public const string Version = "v1.32.0";

    private const string Image = "axllent/mailpit:" + Version;
    private const int SmtpContainerPort = 1025;
    private const int ApiContainerPort = 8025;

    private readonly IContainer _container = new ContainerBuilder(Image)
        .WithPortBinding(SmtpContainerPort, assignRandomHostPort: true)
        .WithPortBinding(ApiContainerPort, assignRandomHostPort: true)
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(request => request.ForPort(ApiContainerPort).ForPath("/readyz")))
        .Build();

    private HttpClient? _api;

    /// <summary>The host the SMTP port is published on (a loopback address or <c>localhost</c>).</summary>
    public string SmtpHost => _container.Hostname;

    public int SmtpPort => _container.GetMappedPublicPort(SmtpContainerPort);

    private HttpClient Api => _api ?? throw new InvalidOperationException("The server has not started.");

    public async ValueTask InitializeAsync()
    {
        await _container.StartAsync();
        _api = new HttpClient
        {
            BaseAddress = new UriBuilder(Uri.UriSchemeHttp, _container.Hostname, _container.GetMappedPublicPort(ApiContainerPort)).Uri,
        };
    }

    /// <summary>The messages to <paramref name="address"/> (newest first): their ids, subjects and Message-IDs.</summary>
    public async Task<IReadOnlyList<MailpitMessage>> MessagesToAsync(string address, CancellationToken cancellationToken)
    {
        var query = Uri.EscapeDataString($"to:\"{address}\"");
        using var document = await Api.GetFromJsonAsync<JsonDocument>(new Uri($"/api/v1/search?query={query}", UriKind.Relative), cancellationToken)
            ?? throw new InvalidOperationException("Mailpit returned no search result.");
        return document.RootElement.GetProperty("messages").EnumerateArray()
            .Select(message => new MailpitMessage(
                message.GetProperty("ID").GetString()!,
                message.GetProperty("MessageID").GetString()!,
                message.GetProperty("Subject").GetString()!))
            .ToList();
    }

    /// <summary>One message's sender, recipients and bodies.</summary>
    public async Task<JsonDocument> MessageAsync(string id, CancellationToken cancellationToken) =>
        await Api.GetFromJsonAsync<JsonDocument>(new Uri($"/api/v1/message/{Uri.EscapeDataString(id)}", UriKind.Relative), cancellationToken)
            ?? throw new InvalidOperationException("Mailpit returned no message.");

    public async ValueTask DisposeAsync()
    {
        _api?.Dispose();
        await _container.DisposeAsync();
    }
}
