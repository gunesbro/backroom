using System.Text;
using System.Text.Json;

namespace Backroom.Modules.Messaging.Application.Messages;

/// <summary>
/// The emoji list the web picker offers (docs/contracts/test-data/picker-emoji.json, embedded in this assembly at build, so the server
/// ships the list it is tested against and cannot drift from it). Used to give a reaction one spelling: a heart with and without
/// U+FE0F is the same emoji, so both become the listed form and count as one reaction.
/// </summary>
internal static class PickerEmojiList
{
    private const string VariationSelector = "️";

    private static readonly Lazy<Dictionary<string, string>> s_listed = new(Load);

    /// <summary>The listed form of the emoji (compared without U+FE0F), or the input when the list has no such emoji.</summary>
    public static string Canonical(string emoji)
    {
        ArgumentNullException.ThrowIfNull(emoji);
        return s_listed.Value.TryGetValue(Key(emoji), out var listed) ? listed : emoji;
    }

    private static string Key(string emoji) => emoji.Replace(VariationSelector, string.Empty, StringComparison.Ordinal);

    private static Dictionary<string, string> Load()
    {
        using var stream = typeof(PickerEmojiList).Assembly.GetManifestResourceStream("picker-emoji.json")
            ?? throw new InvalidOperationException("The emoji list is not embedded in the assembly.");
        using var document = JsonDocument.Parse(stream);
        var listed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var group in document.RootElement.GetProperty("offered").EnumerateObject())
        {
            foreach (var emoji in group.Value.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                var key = Key(emoji);
                // Two spellings of one emoji: the longer one (with the selector) is the fully qualified form.
                if (!listed.TryGetValue(key, out var known) || Encoding.UTF8.GetByteCount(emoji) > Encoding.UTF8.GetByteCount(known))
                {
                    listed[key] = emoji;
                }
            }
        }

        return listed;
    }
}
