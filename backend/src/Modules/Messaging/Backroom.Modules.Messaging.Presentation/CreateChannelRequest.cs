namespace Backroom.Modules.Messaging.Presentation;

public sealed record CreateChannelRequest(string? Name, bool? Private = null, string? Description = null, IReadOnlyList<Guid>? MemberAccountIds = null);
