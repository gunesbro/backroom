using System.Text.Json;
using AwesomeAssertions;
using Backroom.Modules.Messaging.Application.Messages;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>
/// The emoji of a reaction against the list the web picker and the status rule share (docs/contracts/test-data/picker-emoji.json): the
/// picker offers exactly the "offered" emoji, so a reaction must take every one of them that fits in 32 bytes, and must refuse the
/// "refused" samples. No database or host: the rule is called directly.
/// </summary>
public sealed class MessageReactionEmojiTests
{
    private static readonly string s_path = Path.Combine(AppContext.BaseDirectory, "contracts", "picker-emoji.json");

    [Fact]
    public void TryRead_ForEveryEmojiThePickerOffers_AcceptsItUnchangedOrRefusesOnlyForLength()
    {
        using var list = JsonDocument.Parse(File.ReadAllText(s_path));
        var offered = list.RootElement.GetProperty("offered").EnumerateObject()
            .SelectMany(group => group.Value.GetString()!.Split(' '))
            .ToList();

        offered.Should().HaveCountGreaterThan(3400);
        var refused = offered.Where(emoji => !ReactionEmoji.TryRead(emoji, out var read) || read != emoji).ToList();

        // Only sequences longer than 32 bytes may be refused (the longest families with skin tones).
        refused.Should().OnlyContain(emoji => System.Text.Encoding.UTF8.GetByteCount(emoji) > ReactionEmoji.MaxUtf8Bytes);
        refused.Count.Should().BeLessThan(offered.Count / 20, "the byte limit costs only a few rare sequences");
    }

    [Fact]
    public void TryRead_ForEverySampleThatIsNotOneEmoji_RefusesIt()
    {
        using var list = JsonDocument.Parse(File.ReadAllText(s_path));
        var samples = list.RootElement.GetProperty("refused").EnumerateArray().Select(sample => sample.GetProperty("value").GetString()!).ToList();

        samples.Should().HaveCountGreaterThan(10);
        samples.Where(sample => ReactionEmoji.TryRead(sample, out _)).Should().BeEmpty();
    }
}
