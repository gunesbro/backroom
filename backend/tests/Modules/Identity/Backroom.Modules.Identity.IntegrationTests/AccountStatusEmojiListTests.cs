using System.Text.Json;
using AwesomeAssertions;
using Backroom.Modules.Identity.Application;

namespace Backroom.Modules.Identity.IntegrationTests;

/// <summary>
/// The one-emoji rule against the list both sides share (docs/contracts/test-data/picker-emoji.json): the web app's emoji picker
/// offers exactly the "offered" emoji (a web test fails when the two differ), so the server must take every one of them, and it
/// must refuse the "refused" samples, which the picker never offers. No database or host: the rule is called directly.
/// </summary>
public sealed class AccountStatusEmojiListTests
{
    private static readonly string s_path = Path.Combine(AppContext.BaseDirectory, "contracts", "picker-emoji.json");

    [Fact]
    public void TryReadEmoji_ForEveryEmojiThePickerOffers_AcceptsItUnchanged()
    {
        using var list = JsonDocument.Parse(File.ReadAllText(s_path));
        var offered = list.RootElement.GetProperty("offered").EnumerateObject()
            .SelectMany(group => group.Value.GetString()!.Split(' '))
            .ToList();

        offered.Should().HaveCountGreaterThan(3400, "the list holds every emoji of the picker with its skin tones");
        var refused = offered.Where(emoji => !AccountStatusRules.TryReadEmoji(emoji, out var read) || read != emoji).ToList();
        refused.Should().BeEmpty("the picker must not offer what the server refuses");
    }

    [Fact]
    public void TryReadEmoji_ForEverySampleThatIsNotOneEmoji_RefusesIt()
    {
        using var list = JsonDocument.Parse(File.ReadAllText(s_path));
        var samples = list.RootElement.GetProperty("refused").EnumerateArray()
            .Select(sample => (Value: sample.GetProperty("value").GetString()!, Why: sample.GetProperty("why").GetString()!))
            .ToList();

        samples.Should().HaveCountGreaterThan(10);
        var accepted = samples.Where(sample => AccountStatusRules.TryReadEmoji(sample.Value, out _)).Select(sample => sample.Why).ToList();
        accepted.Should().BeEmpty();
    }
}
