namespace Backroom.Modules.Presence.Contracts;

/// <summary>
/// The cell's copy of an account's manual status (<c>AccountStatusChanged</c> on the link, docs/contracts/phase-2.md): stored values,
/// expired ones included, and the control plane's version. <see cref="Away"/> is away chosen by hand; the cell honors it only when the
/// copy carries no do not disturb.
/// </summary>
public sealed record AccountStatusCopy(
    Guid AccountId,
    DateTimeOffset? DndUntil,
    string? CustomEmoji,
    string? CustomText,
    DateTimeOffset? CustomExpiresAt,
    string? TimeZone,
    long Version)
{
    public bool Away { get; init; }
}
