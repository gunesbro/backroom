using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Domain.Errors;

namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>
/// The caller's conversations (docs/contracts/phase-2.md, "HTTP APIs": <c>GET /conversations</c>): the open, unarchived
/// conversations the account is a member of, so a private conversation is listed only to its members. The endpoint
/// has checked the organization membership.
/// </summary>
internal sealed class ConversationService(IConversationStore conversations, IOrganizationContext context)
{
    public const int DefaultPageSize = 50;
    public const int MaxPageSize = 100;

    public static readonly Error PageInvalid =
        Error.Validation("messaging.page_invalid", "The page size must be between 1 and 100.");

    public async Task<Result<ConversationPage>> ListAsync(Guid accountId, Guid? after, int? limit, CancellationToken cancellationToken)
    {
        var pageSize = limit ?? DefaultPageSize;
        if (pageSize is < 1 or > MaxPageSize)
        {
            return PageInvalid;
        }

        var organizationId = context.OrganizationId ?? throw new InvalidOperationException("The request has not entered an organization.");

        // One more than asked for tells whether another page follows.
        var found = await conversations.ListForAccountAsync(organizationId, accountId, after ?? Guid.Empty, pageSize + 1, cancellationToken);
        return found.Count > pageSize
            ? new ConversationPage([.. found.Take(pageSize)], found[pageSize - 1].Id)
            : new ConversationPage(found, null);
    }
}
