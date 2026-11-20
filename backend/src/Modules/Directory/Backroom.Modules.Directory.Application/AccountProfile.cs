using System.Text.RegularExpressions;

namespace Backroom.Modules.Directory.Application;

/// <summary>
/// The account's name and locale as the control plane sends them to a cell (an organization's owner, an accepted
/// invitee). Both come from the token and are editable in Keycloak, so they are checked here.
/// </summary>
internal static partial class AccountProfile
{
    /// <summary>The most user-perceived characters of a display name, as for an organization name.</summary>
    public const int MaxDisplayNameLength = 100;

    /// <summary>The locale when the token has none (or an unusable one).</summary>
    public const string DefaultLocale = "en-US";

    /// <summary>The display name normalized by <see cref="DisplayText"/>, or <paramref name="email"/> if it is missing or unusable.</summary>
    public static string DisplayNameOrEmail(string? displayName, string email) =>
        DisplayText.TryNormalize(displayName, MaxDisplayNameLength, out var normalized) ? normalized : email;

    /// <summary>The locale if it is a BCP 47 tag of the usual shapes; otherwise <see cref="DefaultLocale"/>.</summary>
    public static string LocaleOrDefault(string? locale) =>
        locale is not null && LocalePattern().IsMatch(locale) ? locale : DefaultLocale;

    // A BCP 47 tag of the usual shapes (en, en-US, zh-Hant-TW); at most 35 characters.
    [GeneratedRegex(@"\A[A-Za-z]{2,3}(-[A-Za-z0-9]{2,8}){0,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex LocalePattern();
}
