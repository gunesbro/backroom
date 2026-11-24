using System.Threading.Channels;
using Backroom.Modules.Realtime.Contracts;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace Backroom.EndToEndTests;

/// <summary>A client of a cell's realtime hub over WebSockets with MessagePack, as the web app connects.</summary>
internal sealed class LiveClient : IAsyncDisposable
{
    private readonly HubConnection _connection;
    private readonly Channel<MessagePostedPayload> _messages = Channel.CreateUnbounded<MessagePostedPayload>();
    private readonly Channel<SessionRevokedPayload> _revoked = Channel.CreateUnbounded<SessionRevokedPayload>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private LiveClient(HubConnection connection)
    {
        _connection = connection;
        connection.On<MessagePostedPayload>(LiveEventNames.MessagePosted, payload => _messages.Writer.TryWrite(payload));
        connection.On<SessionRevokedPayload>(LiveEventNames.SessionRevoked, payload => _revoked.Writer.TryWrite(payload));
        connection.Closed += _ =>
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        };
    }

    /// <summary>Completes when the connection ends.</summary>
    public Task Closed => _closed.Task;

    public static async Task<LiveClient> ConnectAsync(Uri cellUrl, AccessToken token, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(token);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(cellUrl, "/hubs/realtime"), options =>
            {
                options.Transports = HttpTransportType.WebSockets;
                options.SkipNegotiation = true;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token.Value);
            })
            .AddMessagePackProtocol()
            .Build();
        var client = new LiveClient(connection);
        try
        {
            await connection.StartAsync(cancellationToken);
            return client;
        }
        catch
        {
            await client.DisposeAsync();
            throw;
        }
    }

    public Task SubscribeOrganizationAsync(Guid organizationId, CancellationToken cancellationToken) =>
        _connection.InvokeAsync("SubscribeOrganization", organizationId, cancellationToken);

    public Task SubscribeChannelAsync(Guid organizationId, Guid channelId, CancellationToken cancellationToken) =>
        _connection.InvokeAsync("SubscribeChannel", organizationId, channelId, cancellationToken);

    public Task<MessagePostedPayload> NextMessageAsync(CancellationToken cancellationToken) =>
        Eventually.WithinDeadlineAsync(_messages.Reader.ReadAsync(cancellationToken).AsTask(), "A MessagePosted event", cancellationToken);

    public Task<SessionRevokedPayload> NextSessionRevokedAsync(CancellationToken cancellationToken) =>
        Eventually.WithinDeadlineAsync(_revoked.Reader.ReadAsync(cancellationToken).AsTask(), "A SessionRevoked event", cancellationToken);

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
