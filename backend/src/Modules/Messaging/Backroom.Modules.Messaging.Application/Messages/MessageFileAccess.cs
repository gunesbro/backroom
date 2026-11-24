using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// No message carries files until attachments arrive (docs/contracts/phase-2.md, "Order of implementation", step 22),
/// so no account can read a file through a message yet. Step 22 replaces this with the query over
/// <c>message_attachments</c> and the conversation's read access.
/// </summary>
internal sealed class MessageFileAccess : IMessageFileAccess
{
    public Task<bool> CanReadFileAsync(Guid organizationId, Guid fileId, Guid accountId, CancellationToken cancellationToken) =>
        Task.FromResult(false);
}
