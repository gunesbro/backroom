using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Google.Protobuf;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.ControlPlane.Contracts.V1;
using Backroom.Modules.CellManagement.Contracts;

namespace Backroom.Modules.CellManagement.Application.Link;

/// <summary>
/// Receives durable upstream messages from a cell (docs/contracts/phase-1.md, "Delivery" and "Scope of
/// upstream messages"):
/// <list type="number">
/// <item>every organization the message names must be mapped to the sending cell, otherwise a
/// non-retryable <c>Nack</c> <c>directory.organization_not_in_cell</c> and a security log event;</item>
/// <item>the message id is recorded and the owning module's <see cref="ICellMessageHandler{T}"/> runs in
/// one unit of work, so a resent message is handled once; then <c>Ack</c>;</item>
/// <item>a type without a handler yet gets a retryable <c>Nack</c> <c>link.not_ready</c>, so the cell keeps it.</item>
/// </list>
/// </summary>
internal sealed partial class UpstreamReceiver(IServiceScopeFactory scopes, TimeProvider timeProvider, ILogger<UpstreamReceiver> logger)
{
    public const string OrganizationNotInCellCode = "directory.organization_not_in_cell";
    public const string NotReadyCode = "link.not_ready";

    [SuppressMessage("Design", "CA1031", Justification = "A failing handler must not end the cell's stream; the cell sends the message again.")]
    public async Task<UpstreamOutcome> ReceiveAsync(string cellId, ConnectRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!Guid.TryParse(request.Envelope?.MessageId, out var messageId))
        {
            return UpstreamOutcome.Reject("invalid_message_id");
        }

        if (TryGetOrganizations(request, out var organizations) is { } invalid)
        {
            return UpstreamOutcome.Reject(invalid);
        }

        await using var scope = scopes.CreateAsyncScope();
        var services = scope.ServiceProvider;
        if (services.GetService<ICellOrganizationScope>() is not { } organizationScope)
        {
            return UpstreamOutcome.Retry(NotReadyCode);
        }

        try
        {
            // The scope check comes first, so a misdirected message is refused and logged even before its
            // type has a handler. It runs before the duplicate check: an organization never changes cells in
            // Phase 1 (moving one needs both checks in one transaction).
            if (organizations.Count > 0 && !await organizationScope.AreInCellAsync(organizations, cellId, cancellationToken))
            {
                LogOrganizationNotInCell(logger, cellId, request.PayloadCase);
                return UpstreamOutcome.Reject(OrganizationNotInCellCode);
            }

            if (!HasHandler(services, request))
            {
                return UpstreamOutcome.Retry(NotReadyCode);
            }

            var unitOfWork = services.GetRequiredService<IUnitOfWork>();
            await unitOfWork.BeginAsync(cancellationToken);
            var type = request.PayloadCase.ToString();
            if (await services.GetRequiredService<IInboundMessages>().TryRecordAsync(messageId, cellId, type, timeProvider.GetUtcNow(), cancellationToken))
            {
                await DispatchAsync(services, new MessageContext(messageId, cellId), request, cancellationToken);
            }

            await unitOfWork.CommitAsync(cancellationToken);
            return UpstreamOutcome.Acknowledged;
        }
        catch (CellMessageRejectedException rejection)
        {
            return UpstreamOutcome.Reject(CodePattern().IsMatch(rejection.Code) ? rejection.Code : "rejected");
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Only the type: a handler's message could carry personal data from the payload.
            LogHandlerFailed(logger, request.PayloadCase, exception.GetType().Name);
            return UpstreamOutcome.Retry("processing_failed");
        }
    }

    // Every organization id the message names; returns a rejection code if one is invalid or there are too many.
    private static string? TryGetOrganizations(ConnectRequest request, out IReadOnlyCollection<Guid> organizations)
    {
        IEnumerable<string> ids = request.PayloadCase switch
        {
            ConnectRequest.PayloadOneofCase.MembershipChanged => [request.MembershipChanged.OrganizationId],
            ConnectRequest.PayloadOneofCase.OrganizationUpdated => [request.OrganizationUpdated.OrganizationId],
            ConnectRequest.PayloadOneofCase.InvitationRegistered => [request.InvitationRegistered.OrganizationId],
            ConnectRequest.PayloadOneofCase.InvitationRevoked => [request.InvitationRevoked.OrganizationId],
            ConnectRequest.PayloadOneofCase.MembershipDigest => request.MembershipDigest.Organizations.Select(digest => digest.OrganizationId),
            ConnectRequest.PayloadOneofCase.MembershipSnapshot =>
                [request.MembershipSnapshot.OrganizationId, .. request.MembershipSnapshot.Members.Select(member => member.OrganizationId)],
            _ => [],
        };

        organizations = [];
        var parsed = new HashSet<Guid>();
        foreach (var id in ids)
        {
            if (!Guid.TryParse(id, out var organizationId) || organizationId == Guid.Empty)
            {
                return "invalid_organization_id";
            }

            if (parsed.Add(organizationId) && parsed.Count > ICellOrganizationScope.MaxOrganizationsPerCheck)
            {
                return "too_many_organizations";
            }
        }

        organizations = parsed;
        return null;
    }

    private static bool HasHandler(IServiceProvider services, ConnectRequest request) =>
        request.PayloadCase switch
        {
            ConnectRequest.PayloadOneofCase.MembershipChanged => HasHandler<MembershipChanged>(services),
            ConnectRequest.PayloadOneofCase.OrganizationUpdated => HasHandler<OrganizationUpdated>(services),
            ConnectRequest.PayloadOneofCase.InvitationRegistered => HasHandler<InvitationRegistered>(services),
            ConnectRequest.PayloadOneofCase.InvitationRevoked => HasHandler<InvitationRevoked>(services),
            ConnectRequest.PayloadOneofCase.MembershipDigest => HasHandler<MembershipDigest>(services),
            ConnectRequest.PayloadOneofCase.MembershipSnapshot => HasHandler<MembershipSnapshot>(services),
            _ => false,
        };

    private static bool HasHandler<T>(IServiceProvider services)
        where T : IMessage => services.GetServices<ICellMessageHandler<T>>().Any();

    private static Task DispatchAsync(IServiceProvider services, MessageContext context, ConnectRequest request, CancellationToken cancellationToken) =>
        request.PayloadCase switch
        {
            ConnectRequest.PayloadOneofCase.MembershipChanged => DispatchAsync(services, context, request.MembershipChanged, cancellationToken),
            ConnectRequest.PayloadOneofCase.OrganizationUpdated => DispatchAsync(services, context, request.OrganizationUpdated, cancellationToken),
            ConnectRequest.PayloadOneofCase.InvitationRegistered => DispatchAsync(services, context, request.InvitationRegistered, cancellationToken),
            ConnectRequest.PayloadOneofCase.InvitationRevoked => DispatchAsync(services, context, request.InvitationRevoked, cancellationToken),
            ConnectRequest.PayloadOneofCase.MembershipDigest => DispatchAsync(services, context, request.MembershipDigest, cancellationToken),
            ConnectRequest.PayloadOneofCase.MembershipSnapshot => DispatchAsync(services, context, request.MembershipSnapshot, cancellationToken),
            _ => throw new InvalidOperationException($"{request.PayloadCase} is not a durable upstream message."),
        };

    private static async Task DispatchAsync<T>(IServiceProvider services, MessageContext context, T payload, CancellationToken cancellationToken)
        where T : IMessage
    {
        var message = new CellMessage<T>(context.MessageId, context.CellId, payload);
        foreach (var handler in services.GetServices<ICellMessageHandler<T>>())
        {
            await handler.HandleAsync(message, cancellationToken);
        }
    }

    [GeneratedRegex(@"\A[a-z0-9_.]{1,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CodePattern();

    [LoggerMessage(Level = LogLevel.Warning, Message = "Security: cell {CellId} sent a {Type} message naming an organization that is not mapped to it; rejected.")]
    private static partial void LogOrganizationNotInCell(ILogger logger, string cellId, ConnectRequest.PayloadOneofCase type);

    [LoggerMessage(Level = LogLevel.Error, Message = "Handling a {Type} link message failed with {ExceptionType}; the cell will send it again.")]
    private static partial void LogHandlerFailed(ILogger logger, ConnectRequest.PayloadOneofCase type, string exceptionType);

    private readonly record struct MessageContext(Guid MessageId, string CellId);
}
