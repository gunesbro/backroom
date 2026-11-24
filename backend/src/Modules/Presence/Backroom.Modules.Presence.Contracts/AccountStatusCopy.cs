namespace Backroom.Modules.Presence.Contracts;

/// <summary>
/// The cell's copy of an account's manual status (<c>AccountStatusChanged</c> on the link, docs/contracts/phase-2.md): stored values,
/// expired ones included, and the control plane's version.
/// </summary>
public sealed record AccountStatusCopy(
    Guid AccountId,
    DateTimeOffset? DndUntil,
    string? CustomEmoji,
    string? CustomText,
    DateTimeOffset? CustomExpiresAt,
    string? TimeZone,
    long Version);
