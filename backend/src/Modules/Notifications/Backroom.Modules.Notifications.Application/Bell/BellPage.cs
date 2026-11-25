namespace Backroom.Modules.Notifications.Application.Bell;

/// <summary>One page of the bell; <paramref name="Next"/> is the cursor of the following page, or null at the end.</summary>
internal sealed record BellPage(IReadOnlyList<BellEntry> Entries, string? Next);
