namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The flag <c>Features:RichText</c> (docs/contracts/phase-2.md, decision 27). On: a send may carry <c>blocks</c>, which
/// the server validates and stores. Off (the default): a send with <c>blocks</c> is refused, so nothing a person
/// formatted is dropped silently; plain <c>text</c> sends work either way, and reads always answer the stored blocks.
/// </summary>
internal sealed record RichText(bool Enabled)
{
    public const string Flag = "Features:RichText";
}
