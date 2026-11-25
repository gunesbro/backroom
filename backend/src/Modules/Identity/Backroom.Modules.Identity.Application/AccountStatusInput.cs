namespace Backroom.Modules.Identity.Application;

/// <summary>A validated, normalized status as the person set it (<see cref="AccountStatusRules"/>).</summary>
internal sealed record AccountStatusInput(
    DateTimeOffset? DndUntil,
    string? CustomEmoji,
    string? CustomText,
    DateTimeOffset? CustomExpiresAt,
    string TimeZone,
    bool Away);
