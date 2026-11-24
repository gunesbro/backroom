using System.Threading.Channels;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Realtime.IntegrationTests;

/// <summary>A hub client on a test node's WebSocket, with the token in the query string like a browser's.</summary>
internal sealed class HubClient : IAsyncDisposable
{
    /// <summary>The longest a test waits for something that must happen; far above what it takes, so only a failure waits it out.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(20);

    private readonly HubConnection _connection;
    private readonly Channel<MessagePostedPayload> _messages = Channel.CreateUnbounded<MessagePostedPayload>();
    private readonly Channel<ChannelCreatedPayload> _channels = Channel.CreateUnbounded<ChannelCreatedPayload>();
    private readonly Channel<SessionRevokedPayload> _revoked = Channel.CreateUnbounded<SessionRevokedPayload>();
    private readonly Channel<PresenceChangedPayload> _presence = Channel.CreateUnbounded<PresenceChangedPayload>();
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);

    private HubClient(HubConnection connection)
    {
        _connection = connection;
        connection.On<MessagePostedPayload>(LiveEventNames.MessagePosted, payload => _messages.Writer.TryWrite(payload));
        connection.On<ChannelCreatedPayload>(LiveEventNames.ChannelCreated, payload => _channels.Writer.TryWrite(payload));
        connection.On<SessionRevokedPayload>(LiveEventNames.SessionRevoked, payload => _revoked.Writer.TryWrite(payload));
        connection.On<PresenceChangedPayload>(LiveEventNames.PresenceChanged, payload => _presence.Writer.TryWrite(payload));
        connection.Closed += _ =>
        {
            _closed.TrySetResult();
            return Task.CompletedTask;
        };
    }

    /// <summary>Completes when the server (or the client) ends the connection.</summary>
    public Task Closed => _closed.Task;

    public HubConnectionState State => _connection.State;

    public static HubClient Create(CellNode node, string token, bool json = false, HttpTransportType? transport = null)
    {
        ArgumentNullException.ThrowIfNull(node);
        var builder = new HubConnectionBuilder()
            .WithUrl(new Uri(node.Server.BaseAddress, "/hubs/realtime"), options =>
            {
                // Another transport negotiates first, with the token in the Authorization header like the JS client.
                options.Transports = transport ?? HttpTransportType.WebSockets;
                options.SkipNegotiation = transport is null;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
                options.HttpMessageHandlerFactory = _ => node.Server.CreateHandler();
                options.WebSocketFactory = async (context, cancellationToken) =>
                    // A browser's WebSocket cannot set headers, so the token goes in the query string, as in the contract.
                    await node.Server.CreateWebSocketClient().ConnectAsync(
                        new Uri($"{context.Uri}{(string.IsNullOrEmpty(context.Uri.Query) ? "?" : "&")}access_token={Uri.EscapeDataString(token)}"), cancellationToken);
            });
        if (json)
        {
            builder.AddJsonProtocol();
        }
        else
        {
            builder.AddMessagePackProtocol();
        }

        return new HubClient(builder.Build());
    }

    public async Task StartAsync()
    {
        using var timeout = new CancellationTokenSource(Deadline);
        await _connection.StartAsync(timeout.Token);
    }

    public Task SubscribeOrganizationAsync(Guid organizationId) => _connection.InvokeAsync("SubscribeOrganization", organizationId);

    public Task UnsubscribeOrganizationAsync(Guid organizationId) => _connection.InvokeAsync("UnsubscribeOrganization", organizationId);

    public Task SubscribeChannelAsync(Guid organizationId, Guid channelId) => _connection.InvokeAsync("SubscribeChannel", organizationId, channelId);

    public Task UnsubscribeChannelAsync(Guid organizationId, Guid channelId) => _connection.InvokeAsync("UnsubscribeChannel", organizationId, channelId);

    public Task WatchPresenceAsync(Guid organizationId, params Guid[] accountIds) => _connection.InvokeAsync("WatchPresence", organizationId, accountIds);

    public Task ReportActivityAsync(string state) => _connection.InvokeAsync("ReportActivity", state);

    public Task<PresenceChangedPayload> NextPresenceAsync() => ReadAsync(_presence.Reader);

    public bool HasUnreadPresence => _presence.Reader.TryPeek(out _);

    public Task<MessagePostedPayload> NextMessageAsync() => ReadAsync(_messages.Reader);

    public Task<ChannelCreatedPayload> NextChannelAsync() => ReadAsync(_channels.Reader);

    /// <summary>The <c>SessionRevoked</c> notice the server sends just before it closes the connection.</summary>
    public Task<SessionRevokedPayload> NextSessionRevokedAsync() => ReadAsync(_revoked.Reader);

    /// <summary>Whether a message has arrived that nobody read; for "nothing was delivered" checks after a later event proved the path.</summary>
    public bool HasUnreadMessage => _messages.Reader.TryPeek(out _);

    public bool HasUnreadChannel => _channels.Reader.TryPeek(out _);

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();

    private static async Task<T> ReadAsync<T>(ChannelReader<T> reader)
    {
        using var timeout = new CancellationTokenSource(Deadline);
        return await reader.ReadAsync(timeout.Token);
    }
}
