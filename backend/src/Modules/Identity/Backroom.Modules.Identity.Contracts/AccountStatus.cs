namespace Backroom.Modules.Identity.Contracts;

/// <summary>
/// An account's manual status and time zone (docs/contracts/phase-2.md, flow 9), as stored: values are kept after they expire,
/// and readers treat an expired <paramref name="DndUntil"/> or <paramref name="CustomExpiresAt"/> as unset.
/// <paramref name="Version"/> grows by one with every change, so a cell applies only newer copies. <see cref="Away"/> is away chosen
/// by hand: it has no end and is never set together with do not disturb.
/// </summary>
public sealed record AccountStatus(
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
