namespace Backroom.Modules.Email.Application.Deliveries;

/// <summary>
/// The value stored for a recipient: a keyed hash of the normalized address, the same for the same address in
/// the cell (so a later block list can match it) and useless without the cell's key (no dictionary of addresses
/// reverses it).
/// </summary>
internal interface IRecipientHasher
{
    /// <summary>32 bytes for a normalized address.</summary>
    byte[] Hash(string normalizedEmail);
}
