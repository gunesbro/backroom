namespace Backroom.Modules.Messaging.Contracts;

/// <summary>
/// Message previews for modules that show a message outside its conversation (the bell, notifications). Goes through the
/// same read check as reading the conversation, so a person who lost access, a deleted message or an unknown id gets
/// no entry. Never logged.
/// </summary>
public interface IMessagePreviews
{
    /// <summary>The most ids one call reads.</summary>
    public const int MaxMessages = 100;

    /// <summary>
    /// The first <paramref name="maxLength"/> characters (plain text, whole text elements) of each message the account may read; the
    /// others are left out of the answer. Runs in the current unit of work, which row-level security limits to the entered organization.
    /// </summary>
    Task<IReadOnlyDictionary<Guid, string>> ReadAsync(
        Guid organizationId, Guid accountId, IReadOnlyCollection<Guid> messageIds, int maxLength, CancellationToken cancellationToken);
}
