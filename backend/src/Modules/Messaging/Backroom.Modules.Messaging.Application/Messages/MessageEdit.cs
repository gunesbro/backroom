namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The flag <c>Features:MessageEdit</c> (docs/contracts/phase-2.md, step 9). Off (the default): editing and deleting
/// messages answer <c>403 messaging.message_edit_unavailable</c> and nothing changes. A delete leaves a tombstone, which
/// the web client does not render yet (it still types <c>text</c> as a string and the author as present), so deleting
/// stays off until the client handles tombstones; the flag is then turned on and removed.
/// </summary>
internal sealed record MessageEdit(bool Enabled)
{
    public const string Flag = "Features:MessageEdit";
}
