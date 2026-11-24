using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Infrastructure.Persistence;
using Backroom.ControlPlane.Contracts.V1;

namespace Backroom.Modules.ControlPlaneConnector.Infrastructure.Link;

/// <summary>
/// Receives durable downstream messages (docs/contracts/phase-1.md, "Delivery"): the message id goes into
/// the inbox and the <see cref="IDownstreamHandler{T}"/> runs in one unit of work, so a resent message is
/// handled once; then <c>Ack</c>. A message about one organization enters that organization first, so its
/// handler works under that organization's row-level security. A type without a handler yet gets a
/// retryable <c>Nack</c> <c>link.not_ready</c>, so the control plane keeps it.
/// </summary>
internal sealed partial class DownstreamReceiver(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<DownstreamReceiver> logger)
{
    public const string NotReadyCode = "link.not_ready";

    private static readonly ActivitySource s_activitySource = new("Backroom.CellLink");

    private const string RecordSql = """
        INSERT INTO control_plane_connector.link_inbox (message_id, type, received_at) VALUES ($1, $2, $3)
        ON CONFLICT DO NOTHING
        """;

    /// <summary>The answer: <see langword="null"/> code for <c>Ack</c>, else a <c>Nack</c>.</summary>
    [SuppressMessage("Design", "CA1031", Justification = "A failing handler must not end the stream; the control plane sends the message again.")]
    public async Task<(string? Code, bool Retryable)> ReceiveAsync(ConnectResponse message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (!Guid.TryParse(message.Envelope?.MessageId, out var messageId))
        {
            return ("invalid_message_id", false);
        }

        if (!TryGetOrganization(message, out var organizationId))
        {
            return ("invalid_organization_id", false);
        }

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;

        // A span per message, linked to the control plane operation that sent it.
        var envelope = message.Envelope!;
        IEnumerable<ActivityLink>? links = ActivityContext.TryParse(envelope.Traceparent, envelope.Tracestate, out var origin) ? [new ActivityLink(origin)] : null;
        using var activity = s_activitySource.StartActivity("cell-link receive", ActivityKind.Consumer, default(ActivityContext), links: links);
        activity?.SetTag("backroom.link.message_type", message.PayloadCase.ToString());
        try
        {
            if (organizationId is { } organization)
            {
                services.GetRequiredService<OrganizationContext>().Enter(organization);
            }

            // Checked without constructing a handler, after the organization is entered.
            if (!HasHandler(services, message))
            {
                return (NotReadyCode, true);
            }

            var session = services.GetRequiredService<DatabaseSession>();
            await session.BeginAsync(cancellationToken);
            if (await RecordAsync(session, messageId, message.PayloadCase.ToString(), cancellationToken))
            {
                await DispatchAsync(services, messageId, message, cancellationToken);
            }

            await session.CommitAsync(cancellationToken);
            return (null, false);
        }
        catch (LinkMessageRejectedException rejection)
        {
            return (CodePattern().IsMatch(rejection.Code) ? rejection.Code : "rejected", false);
        }
        catch (LinkPrerequisiteMissingException missing)
        {
            LogPrerequisiteMissing(logger, message.PayloadCase);
            return (CodePattern().IsMatch(missing.Code) ? missing.Code : "link.prerequisite_missing", true);
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the type: a handler's message could carry personal data from the payload.
            activity?.SetStatus(ActivityStatusCode.Error, exception.GetType().Name);
            LogHandlerFailed(logger, message.PayloadCase, exception.GetType().Name);
            return ("processing_failed", true);
        }
    }

    private async Task<bool> RecordAsync(DatabaseSession session, Guid messageId, string type, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(RecordSql, session.Connection, session.Transaction);
        command.Parameters.Add(new NpgsqlParameter<Guid> { TypedValue = messageId });
        command.Parameters.Add(new NpgsqlParameter<string> { TypedValue = type });
        command.Parameters.Add(new NpgsqlParameter<DateTimeOffset> { TypedValue = timeProvider.GetUtcNow() });
        return await command.ExecuteNonQueryAsync(cancellationToken) == 1;
    }

    // The organization a message is about (null for account-wide ones); false if its id is not valid.
    private static bool TryGetOrganization(ConnectResponse message, out Guid? organizationId)
    {
        var id = message.PayloadCase switch
        {
            ConnectResponse.PayloadOneofCase.OrganizationProvisioned => message.OrganizationProvisioned.OrganizationId,
            ConnectResponse.PayloadOneofCase.InvitationAccepted => message.InvitationAccepted.OrganizationId,
            ConnectResponse.PayloadOneofCase.MembershipSnapshotRequested => message.MembershipSnapshotRequested.OrganizationId,
            _ => null,
        };

        organizationId = null;
        if (id is null)
        {
            return true;
        }

        if (!Guid.TryParse(id, out var parsed) || parsed == Guid.Empty)
        {
            return false;
        }

        organizationId = parsed;
        return true;
    }

    private static bool HasHandler(IServiceProvider services, ConnectResponse message) =>
        message.PayloadCase switch
        {
            ConnectResponse.PayloadOneofCase.OrganizationProvisioned => HasHandler<OrganizationProvisioned>(services),
            ConnectResponse.PayloadOneofCase.SessionRevoked => HasHandler<SessionRevoked>(services),
            ConnectResponse.PayloadOneofCase.InvitationAccepted => HasHandler<InvitationAccepted>(services),
            ConnectResponse.PayloadOneofCase.MembershipSnapshotRequested => HasHandler<MembershipSnapshotRequested>(services),
            ConnectResponse.PayloadOneofCase.AccountStatusChanged => HasHandler<AccountStatusChanged>(services),
            _ => false,
        };

    private static bool HasHandler<T>(IServiceProvider services)
        where T : IMessage => services.GetRequiredService<IServiceProviderIsService>().IsService(typeof(IDownstreamHandler<T>));

    private static Task DispatchAsync(IServiceProvider services, Guid messageId, ConnectResponse message, CancellationToken cancellationToken) =>
        message.PayloadCase switch
        {
            ConnectResponse.PayloadOneofCase.OrganizationProvisioned => DispatchAsync(services, messageId, message.OrganizationProvisioned, cancellationToken),
            ConnectResponse.PayloadOneofCase.SessionRevoked => DispatchAsync(services, messageId, message.SessionRevoked, cancellationToken),
            ConnectResponse.PayloadOneofCase.InvitationAccepted => DispatchAsync(services, messageId, message.InvitationAccepted, cancellationToken),
            ConnectResponse.PayloadOneofCase.MembershipSnapshotRequested => DispatchAsync(services, messageId, message.MembershipSnapshotRequested, cancellationToken),
            ConnectResponse.PayloadOneofCase.AccountStatusChanged => DispatchAsync(services, messageId, message.AccountStatusChanged, cancellationToken),
            _ => throw new InvalidOperationException($"{message.PayloadCase} is not a durable downstream message."),
        };

    private static async Task DispatchAsync<T>(IServiceProvider services, Guid messageId, T payload, CancellationToken cancellationToken)
        where T : IMessage
    {
        foreach (var handler in services.GetServices<IDownstreamHandler<T>>())
        {
            await handler.HandleAsync(messageId, payload, cancellationToken);
        }
    }

    [GeneratedRegex(@"\A[a-z0-9_.]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling a {Type} link message failed with {ExceptionType}; the control plane will send it again.")]
    private static partial void LogHandlerFailed(ILogger logger, ConnectResponse.PayloadOneofCase type, string exceptionType);

    [LoggerMessage(Level = LogLevel.Warning, Message = "A {Type} link message waits for something the cell has not seen yet; the control plane will send it again.")]
    private static partial void LogPrerequisiteMissing(ILogger logger, ConnectResponse.PayloadOneofCase type);
}
