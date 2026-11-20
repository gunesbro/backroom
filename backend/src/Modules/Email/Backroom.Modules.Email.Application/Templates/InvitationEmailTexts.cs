using System.Text.Json;

namespace Backroom.Modules.Email.Application.Templates;

/// <summary>
/// The invitation template's texts in one language, from the embedded translation files
/// (<c>Templates/invitation-v1-{language}.json</c>). <c>{organization}</c> and <c>{product}</c> are filled in by
/// <see cref="InvitationEmail"/>.
/// </summary>
internal sealed record InvitationEmailTexts(
    string Language,
    string SenderName,
    string UnnamedSenderName,
    string Subject,
    string Intro,
    string Button,
    string LinkHint,
    string CopyHint,
    string Limits,
    string Unexpected,
    string UnnamedOrganization)
{
    public const string English = "en";

    public const string Turkish = "tr";

    private static readonly Lazy<InvitationEmailTexts> s_english = new(() => Load(English));

    private static readonly Lazy<InvitationEmailTexts> s_turkish = new(() => Load(Turkish));

    /// <summary>The texts for a BCP 47 tag such as <c>en-US</c> or <c>tr-TR</c>; English for any other language.</summary>
    public static InvitationEmailTexts For(string? locale)
    {
        var separator = locale?.IndexOf('-', StringComparison.Ordinal) ?? -1;
        var language = separator < 0 ? locale : locale![..separator];
        return string.Equals(language, Turkish, StringComparison.OrdinalIgnoreCase) ? s_turkish.Value : s_english.Value;
    }

    private static InvitationEmailTexts Load(string language)
    {
        var name = $"invitation-v1-{language}.json";
        using var stream = typeof(InvitationEmailTexts).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"The template texts {name} are missing.");
        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;
        string Text(string key) =>
            root.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String && value.GetString() is { Length: > 0 } text
                ? text
                : throw new InvalidOperationException($"The template texts {name} have no '{key}'.");

        return new InvitationEmailTexts(
            language,
            Text("senderName"),
            Text("unnamedSenderName"),
            Text("subject"),
            Text("intro"),
            Text("button"),
            Text("linkHint"),
            Text("copyHint"),
            Text("limits"),
            Text("unexpected"),
            Text("unnamedOrganization"));
    }
}
