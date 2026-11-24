using System.Globalization;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>Group names exactly as the contract lists them (docs/contracts/phase-1.md, "Live events").</summary>
internal static class GroupNames
{
    public static string Organization(string cellId, Guid organizationId) =>
        string.Create(CultureInfo.InvariantCulture, $"cell:{cellId}:org:{organizationId:D}");

    public static string Channel(string cellId, Guid organizationId, Guid channelId) =>
        string.Create(CultureInfo.InvariantCulture, $"cell:{cellId}:org:{organizationId:D}:channel:{channelId:D}");
}
