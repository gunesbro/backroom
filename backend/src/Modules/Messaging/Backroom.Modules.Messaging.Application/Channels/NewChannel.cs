namespace Backroom.Modules.Messaging.Application.Channels;

internal sealed record NewChannel(Guid Id, Guid OrgId, string Name, string NameKey, Guid CreatedBy, DateTimeOffset CreatedAt);
