namespace Backroom.Modules.Notifications.Application.Bell;

/// <summary>A bell entry as shown: <paramref name="Preview"/> is null when the person can no longer read the message (or there is none).</summary>
internal sealed record BellEntry(InboxItem Item, string? Preview);
