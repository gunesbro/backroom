namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>What adding a reaction did: <see cref="Added"/>, found it <see cref="Exists"/>, or was refused by a limit.</summary>
internal enum ReactionAdd
{
    Added,
    Exists,

    /// <summary>The person already has the most reactions they may have on the message (20).</summary>
    PersonLimit,

    /// <summary>The message already has the most distinct emoji it may have (50) and this one is not among them.</summary>
    EmojiLimit,
}
