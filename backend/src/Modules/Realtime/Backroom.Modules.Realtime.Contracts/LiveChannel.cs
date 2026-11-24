namespace Backroom.Modules.Realtime.Contracts;

/// <summary>A channel as live clients receive it.</summary>
public sealed record LiveChannel(Guid Id, string Name, DateTimeOffset CreatedAt);
