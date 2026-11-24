using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>A channel as live clients receive it.</summary>
[MessagePackObject]
public sealed record LiveChannel(
    [property: Key("id")] Guid Id,
    [property: Key("name")] string Name,
    [property: Key("createdAt")] DateTimeOffset CreatedAt);
