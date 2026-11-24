using Backroom.Modules.Files.Application.Uploads;
using Backroom.Modules.Messaging.Contracts;

namespace Backroom.Modules.Files.Application.Access;

/// <summary>
/// Who may upload to a conversation and who may download a file (docs/contracts/phase-2.md, flow 6, steps 1 and 5),
/// asked anew on every request. The endpoint has checked the organization membership.
/// </summary>
internal sealed class FileAccessCheck(IChannelReadAccess conversations, IMessageFileAccess messages)
{
    /// <summary>
    /// Whether the account may post in the conversation. Phase 1 conversations are open public channels, where every
    /// member may post; the conversations of Phase 2 (step 1) bring their own answer through Messaging's contract.
    /// </summary>
    public Task<bool> CanPostAsync(Guid organizationId, Guid conversationId, Guid accountId, CancellationToken cancellationToken) =>
        conversations.CanReadAsync(organizationId, conversationId, accountId, cancellationToken);

    /// <summary>
    /// A ready upload that no message carries yet: only its uploader, and only while they can still read its
    /// conversation. An attached file: whoever can read a message that carries it (Messaging decides, step 22).
    /// Every other state (pending, scanning, rejected, deleted, expired) is refused.
    /// </summary>
    public async Task<bool> CanDownloadAsync(Guid organizationId, FileRecord file, Guid accountId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(file);
        return file.Status switch
        {
            FileStatus.Ready => file.UploaderAccountId == accountId
                && await conversations.CanReadAsync(organizationId, file.ConversationId, accountId, cancellationToken),
            FileStatus.Attached => await messages.CanReadFileAsync(organizationId, file.Id, accountId, cancellationToken),
            _ => false,
        };
    }
}
