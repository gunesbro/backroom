using MessagePack;

namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// One emoji of a message's reaction summary in <c>ReactionsChanged</c>: how many people reacted with it and up to three of them
/// (the earliest). <c>reactedByMe</c> is not sent: it depends on the reader, and the client fills it from its own state.
/// </summary>
[MessagePackObject]
public sealed record LiveReaction(
    [property: Key("emoji")] string Emoji,
    [property: Key("count")] int Count,
    [property: Key("sample")] IReadOnlyList<Guid> Sample);
