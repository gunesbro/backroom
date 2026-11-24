using System.Globalization;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>Group names exactly as the contracts list them (docs/contracts/phase-1.md and phase-2.md, "Live events").</summary>
internal static class GroupNames
{
    public static string Organization(string cellId, Guid organizationId) =>
        string.Create(CultureInfo.InvariantCulture, $"cell:{cellId}:org:{organizationId:D}");

    public static string Presence(string cellId, Guid organizationId, Guid accountId) =>
        string.Create(CultureInfo.InvariantCulture, $"cell:{cellId}:org:{organizationId:D}:presence:{accountId:D}");

    public static string Channel(string cellId, Guid organizationId, Guid channelId) =>
        string.Create(CultureInfo.InvariantCulture, $"cell:{cellId}:org:{organizationId:D}:channel:{channelId:D}");
}
