namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>How many of a person's recent actions fall in a window (counted up to a cap) and when the oldest of those happened.</summary>
internal sealed record RecentCount(int Count, DateTimeOffset? Oldest);
