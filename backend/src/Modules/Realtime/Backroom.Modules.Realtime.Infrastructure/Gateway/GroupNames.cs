using System.Globalization;

namespace Backroom.Modules.Realtime.Infrastructure.Gateway;

/// <summary>
/// Group names exactly as the contract lists them (docs/contracts/phase-2.md, "Live events"). Every group of an
/// organization starts with <see cref="Organization"/>'s name.
/// </summary>
internal static class GroupNames
{
    public static string Organization(string cellId, Guid organizationId) =>
        string.Create(CultureInfo.InvariantCulture, $"cell:{cellId}:org:{organizationId:D}");

    /// <summary>A conversation of any kind (replaces Phase 1's <c>channel:{id}</c>); thread events go to the parent's group.</summary>
    public static string Conversation(string cellId, Guid organizationId, Guid conversationId) =>
        string.Create(CultureInfo.InvariantCulture, $"cell:{cellId}:org:{organizationId:D}:conversation:{conversationId:D}");

    /// <summary>
    /// One account's own connections in one organization, for per-person events (read state, seen, activity). Only the
    /// hub adds to it, and only connections whose token names this account.
    /// </summary>
    public static string Account(string cellId, Guid organizationId, Guid accountId) =>
        string.Create(CultureInfo.InvariantCulture, $"cell:{cellId}:org:{organizationId:D}:account:{accountId:D}");
}
