namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The flag <c>Features:MessageReactions</c> (docs/contracts/phase-2.md, flow 2 "Reactions"). Off (the default): the reaction
/// endpoints answer <c>404</c> as if they did not exist, and no read carries <c>reactions</c>. It stays off until the web renders
/// reactions; then it is turned on and removed. Not in the contract's flag list.
/// </summary>
internal sealed record MessageReactions(bool Enabled)
{
    public const string Flag = "Features:MessageReactions";
}
