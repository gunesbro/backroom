namespace Backroom.Modules.Messaging.Presentation;

public sealed record AddMembersRequest(IReadOnlyList<Guid>? AccountIds);
