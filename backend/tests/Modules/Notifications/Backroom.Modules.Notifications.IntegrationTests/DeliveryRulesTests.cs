using AwesomeAssertions;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Notifications.Application.Delivery;
using Backroom.Modules.Notifications.Application.Settings;

namespace Backroom.Modules.Notifications.IntegrationTests;

/// <summary>The decision order of flow 8 as a pure function (docs/contracts/phase-2.md).</summary>
public sealed class DeliveryRulesTests
{
    private static readonly DateTimeOffset s_now = new(2026, 11, 25, 12, 0, 0, TimeSpan.Zero);

    private static ConversationSettings Conversation(NotificationLevel level, bool followed = true, bool ignoreGroup = false) =>
        new(level, followed, ignoreGroup, Sound: true);

    [Fact]
    public void Decide_WhenNoSettingsExist_DeliversEverything()
    {
        DeliveryRules.Decide(ActivityKinds.DirectMessage, doNotDisturb: false, null, AccountSettings.Default, s_now).Should().Be(Outcome.Deliver);
        DeliveryRules.Decide(ActivityKinds.Mention, doNotDisturb: false, null, AccountSettings.Default, s_now).Should().Be(Outcome.Deliver);
    }

    [Fact]
    public void Decide_WhenDoNotDisturbIsOn_CollectsInTheBellSilently()
    {
        DeliveryRules.Decide(ActivityKinds.Mention, doNotDisturb: true, null, AccountSettings.Default, s_now).Should().Be(Outcome.BellOnly);
        DeliveryRules.Decide(ActivityKinds.Mention, doNotDisturb: true, Conversation(NotificationLevel.All), AccountSettings.Default, s_now)
            .Should().Be(Outcome.BellOnly);
    }

    [Fact]
    public void Decide_WhenDoNotDisturbIsOnAndSettingsSayNothing_AddsNoEntry()
    {
        DeliveryRules.Decide(ActivityKinds.Mention, doNotDisturb: true, Conversation(NotificationLevel.None), AccountSettings.Default, s_now)
            .Should().Be(Outcome.Drop);
    }

    [Fact]
    public void Decide_WhenConversationIsSetToMentions_DropsPlainDirectMessages()
    {
        var settings = Conversation(NotificationLevel.Mentions);

        DeliveryRules.Decide(ActivityKinds.DirectMessage, false, settings, AccountSettings.Default, s_now).Should().Be(Outcome.Drop);
        DeliveryRules.Decide(ActivityKinds.Mention, false, settings, AccountSettings.Default, s_now).Should().Be(Outcome.Deliver);
        DeliveryRules.Decide(ActivityKinds.QuotedReply, false, settings, AccountSettings.Default, s_now).Should().Be(Outcome.Deliver);
    }

    [Fact]
    public void Decide_WhenConversationIsSetToNothing_DropsEvenMentions()
    {
        DeliveryRules.Decide(ActivityKinds.Mention, false, Conversation(NotificationLevel.None), AccountSettings.Default, s_now).Should().Be(Outcome.Drop);
    }

    [Fact]
    public void Decide_WhenGroupMentionsAreIgnored_DropsOnlyThose()
    {
        var settings = Conversation(NotificationLevel.All, ignoreGroup: true);

        DeliveryRules.Decide(ActivityKinds.GroupMention, false, settings, AccountSettings.Default, s_now).Should().Be(Outcome.Drop);
        DeliveryRules.Decide(ActivityKinds.Mention, false, settings, AccountSettings.Default, s_now).Should().Be(Outcome.Deliver);
    }

    [Fact]
    public void Decide_WhenThreadRepliesAreNotFollowed_DropsThemUnderMentions()
    {
        DeliveryRules.Decide(ActivityKinds.ThreadReply, false, Conversation(NotificationLevel.Mentions, followed: false), AccountSettings.Default, s_now)
            .Should().Be(Outcome.Drop);
        DeliveryRules.Decide(ActivityKinds.ThreadReply, false, Conversation(NotificationLevel.Mentions, followed: true), AccountSettings.Default, s_now)
            .Should().Be(Outcome.Deliver);
    }

    [Fact]
    public void Decide_WhenOrganizationIsMuted_CollectsInTheBellSilentlyUntilTheMuteEnds()
    {
        var muted = new AccountSettings(NotificationLevel.All, s_now.AddHours(1), true, false);

        DeliveryRules.Decide(ActivityKinds.Mention, false, null, muted, s_now).Should().Be(Outcome.BellOnly);
        DeliveryRules.Decide(ActivityKinds.Mention, false, null, muted, s_now.AddHours(2)).Should().Be(Outcome.Deliver);
    }

    [Fact]
    public void Decide_WhenConversationHasItsOwnSetting_ItBeatsTheOrganizationMute()
    {
        var muted = new AccountSettings(NotificationLevel.All, s_now.AddHours(1), true, false);

        DeliveryRules.Decide(ActivityKinds.Mention, false, Conversation(NotificationLevel.All), muted, s_now).Should().Be(Outcome.Deliver);
    }

    [Fact]
    public void Decide_WhenAccountDefaultIsMentions_AppliesWhereTheConversationHasNoSetting()
    {
        var account = new AccountSettings(NotificationLevel.Mentions, null, true, false);

        DeliveryRules.Decide(ActivityKinds.DirectMessage, false, null, account, s_now).Should().Be(Outcome.Drop);
        DeliveryRules.Decide(ActivityKinds.ReminderDue, false, null, account, s_now).Should().Be(Outcome.Deliver);
    }
}
