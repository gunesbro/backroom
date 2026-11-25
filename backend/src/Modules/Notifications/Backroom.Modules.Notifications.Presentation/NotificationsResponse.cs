namespace Backroom.Modules.Notifications.Presentation;

public sealed record NotificationsResponse(IReadOnlyList<NotificationResponse> Items, string? Next);
