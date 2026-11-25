using System.Text.Json.Serialization;
using Backroom.Modules.Identity.Contracts;

namespace Backroom.Modules.Identity.Presentation;

/// <summary>The own status; expired parts are null. The time zone and whether away was chosen by hand are returned only here, to their owner.</summary>
internal sealed record AccountStatusResponse(
    [property: JsonPropertyName("dndUntil")] DateTimeOffset? DndUntil,
    [property: JsonPropertyName("away")] bool Away,
    [property: JsonPropertyName("customStatus")] CustomStatusBody? CustomStatus,
    [property: JsonPropertyName("timeZone")] string? TimeZone,
    [property: JsonPropertyName("version")] long Version)
{
    public static AccountStatusResponse From(AccountStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        var custom = status.CustomEmoji is null && status.CustomText is null
            ? null
            : new CustomStatusBody(status.CustomEmoji, status.CustomText, status.CustomExpiresAt);
        return new AccountStatusResponse(status.DndUntil, status.Away, custom, status.TimeZone, status.Version);
    }
}
