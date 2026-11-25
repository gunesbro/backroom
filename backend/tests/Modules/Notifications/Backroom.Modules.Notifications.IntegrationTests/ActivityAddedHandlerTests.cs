using AwesomeAssertions;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Realtime.Contracts;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Notifications.IntegrationTests;

/// <summary>The consumer of <c>messaging.activity-added.v1</c>: the decision order applied to stored settings, and the live event.</summary>
public sealed class ActivityAddedHandlerTests(PostgresDatabase database) : NotificationsTestBase(database)
{
    private readonly Guid _org = Guid.CreateVersion7();
    private readonly Guid _conversation = Guid.CreateVersion7();
    private readonly Guid _author = Guid.CreateVersion7();
    private readonly Guid _bob = Guid.CreateVersion7();

    [Fact]
    public async Task Handle_WhenSettingsDeliver_StoresTheEntryAndAnnouncesItToTheRecipientAsync()
    {
        var message = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob);
        Previews.Add(message, _conversation, "hello Bob");

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, message, _author, _bob));

        var live = Published.Notifications.Should().ContainSingle().Subject;
        live.AccountId.Should().Be(_bob);
        live.OrgId.Should().Be(_org);
        live.Kind.Should().Be(ActivityKinds.Mention);
        live.MessageId.Should().Be(message);
        live.ActorAccountId.Should().Be(_author);
        live.Preview.Should().Be("hello Bob");
        (await ListAsync(_org, _bob)).Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_WhenRecipientHasDoNotDisturb_LandsInTheBellSilentlyAsync()
    {
        var message = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob);
        Previews.Add(message, _conversation, "ping");
        Presence.DoNotDisturb(_bob, Clock.GetUtcNow().AddHours(1));

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, message, _author, _bob));

        Published.Notifications.Should().BeEmpty();
        var page = await ListAsync(_org, _bob);
        page.Entries.Should().ContainSingle().Which.Preview.Should().Be("ping");
    }

    [Fact]
    public async Task Handle_WhenDoNotDisturbHasEnded_AnnouncesAgainAsync()
    {
        Access.Allow(_conversation, _bob);
        Presence.DoNotDisturb(_bob, Clock.GetUtcNow().AddMinutes(-1));

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, Guid.CreateVersion7(), _author, _bob));

        Published.Notifications.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_WhenConversationIsSetToNothing_AddsNoEntryAndAnnouncesNothingAsync()
    {
        await SaveConversationSettingsAsync(_org, _bob, _conversation, "none");

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, Guid.CreateVersion7(), _author, _bob));

        Published.Notifications.Should().BeEmpty();
        (await ListAsync(_org, _bob)).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenOrganizationIsMuted_CollectsSilentlyAsync()
    {
        Access.Allow(_conversation, _bob);
        await SaveAccountSettingsAsync(_org, _bob, "all", mutedUntil: Clock.GetUtcNow().AddHours(1));

        await HandleAsync(_org, Activity(_org, ActivityKinds.DirectMessage, _conversation, Guid.CreateVersion7(), _author, _bob));

        Published.Notifications.Should().BeEmpty();
        (await ListAsync(_org, _bob)).Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_WhenPreviewsAreHidden_TheLiveEventCarriesNoTextAsync()
    {
        var message = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob);
        Previews.Add(message, _conversation, "secret words");
        await SaveAccountSettingsAsync(_org, _bob, "all", hidePreviews: true);

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, message, _author, _bob));

        Published.Notifications.Should().ContainSingle().Which.Preview.Should().BeNull();
    }

    [Fact]
    public async Task Handle_AsksForAtMost150CharactersAsync()
    {
        var message = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob);
        Previews.Add(message, _conversation, new string('x', 400));

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, message, _author, _bob));

        Published.Notifications.Single().Preview!.Length.Should().Be(NotificationRequestedPayload.MaxPreviewLength);
        Previews.RequestedLengths.Should().OnlyContain(length => length == 150);
    }

    [Fact]
    public async Task Handle_ForTheActor_AddsNothingAsync()
    {
        Access.Allow(_conversation, _author);

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, Guid.CreateVersion7(), _author, _author));

        Published.Notifications.Should().BeEmpty();
        (await ListAsync(_org, _author)).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenTheSameEventIsDeliveredTwice_AddsAndAnnouncesOnceAsync()
    {
        var message = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob);
        var activity = Activity(_org, ActivityKinds.Mention, _conversation, message, _author, _bob);

        await HandleAsync(_org, activity);
        await HandleAsync(_org, activity with { EventId = Guid.CreateVersion7() });

        Published.Notifications.Should().ContainSingle();
        (await ListAsync(_org, _bob)).Entries.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_WhenAMessageNotifiesTheSamePersonAsAnotherKind_KeepsOneEntryAsync()
    {
        var message = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob);

        await HandleAsync(_org, Activity(_org, ActivityKinds.QuotedReply, _conversation, message, _author, _bob));
        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, message, _author, _bob));

        (await ListAsync(_org, _bob)).Entries.Should().ContainSingle().Which.Item.Kind.Should().Be(ActivityKinds.QuotedReply);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Handle_WhenRecipientCannotReadTheConversation_NotifiesNobodyAsync()
    {
        var carol = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob);

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, Guid.CreateVersion7(), _author, _bob, carol));

        Published.Notifications.Should().ContainSingle().Which.AccountId.Should().Be(_bob);
        (await ListAsync(_org, carol)).Entries.Should().BeEmpty();
        (await CountAsync("SELECT count(*) FROM notifications.inbox_items WHERE account_id = $1", carol)).Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Handle_SendsTheLiveEventOnlyToTheNamedRecipientAsync()
    {
        var carol = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob, carol);

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, Guid.CreateVersion7(), _author, _bob));

        Published.Notifications.Should().ContainSingle().Which.AccountId.Should().Be(_bob);
        (await ListAsync(_org, carol)).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ForKindsWithoutAConversation_NeedsNoReadCheckAsync()
    {
        await HandleAsync(_org, Activity(_org, ActivityKinds.InvitationAccepted, null, null, _author, _bob));

        Published.Notifications.Should().ContainSingle().Which.ConversationId.Should().BeNull();
    }

    [Fact]
    public async Task Handle_WhenTheKindIsUnknown_IgnoresTheEventAsync()
    {
        Access.Allow(_conversation, _bob);

        await HandleAsync(_org, Activity(_org, "something_new", _conversation, Guid.CreateVersion7(), _author, _bob));

        Published.Notifications.Should().BeEmpty();
        (await ListAsync(_org, _bob)).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_WhenTheUnitOfWorkDoesNotCommit_AnnouncesNothingAndTheRedeliveryAnnouncesOnceAsync()
    {
        var message = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob);
        var activity = Activity(_org, ActivityKinds.Mention, _conversation, message, _author, _bob);

        await HandleWithoutCommitAsync(_org, activity);

        Published.Notifications.Should().BeEmpty();
        (await ListAsync(_org, _bob)).Entries.Should().BeEmpty();

        await HandleAsync(_org, activity);
        await HandleAsync(_org, activity);

        Published.Notifications.Should().ContainSingle();
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Handle_WhenARecipientWasRemovedFromTheOrganizationOrIsAGuest_NotifiesThemNotAsync()
    {
        var removed = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob, removed);
        Members.Remove(removed);

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, Guid.CreateVersion7(), _author, _bob, removed));
        await HandleAsync(_org, Activity(_org, ActivityKinds.InvitationAccepted, null, null, _author, removed));

        Published.Notifications.Should().ContainSingle().Which.AccountId.Should().Be(_bob);
        (await ListAsync(_org, removed)).Entries.Should().BeEmpty();
        (await CountAsync("SELECT count(*) FROM notifications.inbox_items WHERE account_id = $1", removed)).Should().Be(0);
    }

    [Fact]
    public async Task Handle_ReadsThePreviewOncePerMessageForAllRecipientsAsync()
    {
        var carol = Guid.CreateVersion7();
        var message = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob, carol);
        Previews.Add(message, _conversation, "hello");

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, message, _author, _bob, carol));

        Published.Notifications.Should().HaveCount(2).And.OnlyContain(live => live.Preview == "hello");
        Previews.RequestedLengths.Should().ContainSingle();
    }

    [Fact]
    public async Task Handle_WhenAMessageHasNoConversation_IgnoresTheEventAsync()
    {
        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, null, Guid.CreateVersion7(), _author, _bob));

        Published.Notifications.Should().BeEmpty();
        (await ListAsync(_org, _bob)).Entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Handle_ForRemindersAboutAMessageThatAlreadyNotified_StoresThemTooAsync()
    {
        var message = Guid.CreateVersion7();
        Access.Allow(_conversation, _bob);

        await HandleAsync(_org, Activity(_org, ActivityKinds.Mention, _conversation, message, _author, _bob));
        await HandleAsync(_org, Activity(_org, ActivityKinds.ReminderDue, _conversation, message, _author, _bob));
        await HandleAsync(_org, Activity(_org, ActivityKinds.ScheduledFailed, _conversation, message, _author, _bob));
        await HandleAsync(_org, Activity(_org, ActivityKinds.ReminderDue, _conversation, message, _author, _bob));

        (await ListAsync(_org, _bob)).Entries.Select(entry => entry.Item.Kind).Should()
            .BeEquivalentTo([ActivityKinds.Mention, ActivityKinds.ReminderDue, ActivityKinds.ScheduledFailed]);
    }
}
