using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>Read access for other modules (<see cref="ConversationAccess.CanRead"/>); one primary-key probe of each table.</summary>
internal sealed class ConversationReadAccess(IConversationStore conversations) : IConversationReadAccess
{
    public async Task<bool> CanReadAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken) =>
        await conversations.FindAsync(organizationId, conversationId, accountId, cancellationToken) is { CanRead: true };
}
