namespace Backroom.Modules.Messaging.Contracts;

/// <summary>The kinds of conversation (docs/contracts/phase-2.md, flow 1), as stored and as carried in events.</summary>
public static class ConversationKinds
{
    public const string PublicChannel = "public_channel";
    public const string PrivateChannel = "private_channel";
    public const string Dm = "dm";
    public const string GroupDm = "group_dm";
    public const string Thread = "thread";
}
