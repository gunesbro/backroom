namespace Backroom.Modules.Messaging.Application.Channels;

/// <param name="Kind">A channel kind of <c>ConversationKinds</c>.</param>
/// <param name="OwnerAccountId">The owner of a private channel; <see langword="null"/> for a public one.</param>
internal sealed record NewChannel(
    Guid Id, Guid OrgId, string Kind, string Name, string NameKey, string? Description, Guid? OwnerAccountId, Guid CreatedBy, DateTimeOffset CreatedAt);
