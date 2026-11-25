using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Realtime.Contracts;

namespace Backroom.Modules.Notifications.Application.Bell;

/// <summary>
/// The bell of the signed-in account (docs/contracts/phase-2.md, flow 8 and "HTTP APIs"): entries newest first, mark read.
/// The endpoint has checked the organization membership. Entries hold ids only; the text shown with an entry is read
/// through Messaging's permission check on every list, so a person who lost access to the conversation sees none.
/// </summary>
internal sealed class BellService(
    INotificationStore store,
    IConversationReadAccess conversations,
    IMessagePreviews previews,
    IOrganizationContext context,
    TimeProvider timeProvider)
{
    public const int DefaultPageSize = 30;
    public const int MaxPageSize = 100;

    public static readonly Error PageInvalid =
        Error.Validation("notifications.page_invalid", "The page size must be between 1 and 100 and the cursor must come from a previous page.");

    public static readonly Error TabInvalid =
        Error.Validation("notifications.tab_invalid", "The tab must be all or mentions.");

    public static readonly Error NotFound =
        Error.NotFound("notifications.not_found", "The notification was not found.");

    public async Task<Result<BellPage>> ListAsync(Guid accountId, string? tab, string? after, int? limit, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            return PageInvalid;
        }

        bool mentionsOnly;
        switch (tab)
        {
            case null or "all":
                mentionsOnly = false;
                break;
            case "mentions":
                mentionsOnly = true;
                break;
            default:
                return TabInvalid;
        }

        (DateTimeOffset CreatedAt, Guid Id)? cursor = null;
        if (after is not null)
        {
            if (!BellCursor.TryDecode(after, out var createdAt, out var id))
            {
                return PageInvalid;
            }

            cursor = (createdAt, id);
        }

        var organizationId = OrganizationId;
        var items = await store.ListAsync(organizationId, accountId, mentionsOnly, cursor, pageSize + 1, cancellationToken);
        var hasMore = items.Count > pageSize;
        var fetched = hasMore ? [.. items.Take(pageSize)] : items;

        // Entries of conversations the person can no longer read are left out (one batch check per page); the cursor still
        // follows the rows fetched, so paging never repeats or skips.
        var conversationIds = fetched.Where(item => item.ConversationId is not null).Select(item => item.ConversationId!.Value).Distinct().ToArray();
        var readable = conversationIds.Length == 0
            ? new HashSet<Guid>()
            : await conversations.ReadableConversationsAsync(organizationId, accountId, conversationIds, cancellationToken);
        var page = fetched.Where(item => item.ConversationId is not Guid conversation || readable.Contains(conversation)).ToList();

        var messageIds = page.Where(item => item.MessageId is not null).Select(item => item.MessageId!.Value).Distinct().ToArray();
        var texts = messageIds.Length == 0
            ? new Dictionary<Guid, string>()
            : await previews.ReadAsync(organizationId, accountId, messageIds, NotificationRequestedPayload.MaxPreviewLength, cancellationToken);

        var entries = page
            .Select(item => new BellEntry(item, item.MessageId is { } messageId && texts.TryGetValue(messageId, out var text) ? text : null))
            .ToArray();
        var next = hasMore ? BellCursor.Encode(fetched[^1].CreatedAt, fetched[^1].Id) : null;
        return new BellPage(entries, next);
    }

    public async Task<Result> MarkReadAsync(Guid accountId, Guid itemId, CancellationToken cancellationToken)
    {
        var marked = await store.MarkReadAsync(OrganizationId, accountId, itemId, StoredTime.Now(timeProvider), cancellationToken);
        return marked ? Result.Success : NotFound;
    }

    public async Task MarkAllReadAsync(Guid accountId, CancellationToken cancellationToken) =>
        await store.MarkAllReadAsync(OrganizationId, accountId, StoredTime.Now(timeProvider), cancellationToken);

    private Guid OrganizationId =>
        context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");
}
