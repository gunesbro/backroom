using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Messaging.Contracts;
using Backroom.Modules.Notifications.Application.Bell;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Notifications.IntegrationTests;

/// <summary>The bell: keyset pages, tabs, mark read, previews through the permission check, and isolation.</summary>
public sealed class BellTests(PostgresDatabase database) : NotificationsTestBase(database)
{
    private readonly Guid _org = Guid.CreateVersion7();
    private readonly Guid _conversation = Guid.CreateVersion7();
    private readonly Guid _author = Guid.CreateVersion7();
    private readonly Guid _bob = Guid.CreateVersion7();

    private async Task<Guid> NotifyAsync(Guid organizationId, Guid account, string kind, string text = "hi")
    {
        var message = Guid.CreateVersion7();
        Access.Allow(_conversation, account);
        Previews.Add(message, _conversation, text);
        await HandleAsync(organizationId, Activity(organizationId, kind, _conversation, message, _author, account));
        Clock.Advance(TimeSpan.FromSeconds(1));
        return message;
    }

    [Fact]
    public async Task List_PagesNewestFirstByKeysetWithoutRepeatsAsync()
    {
        for (var i = 0; i < 5; i++)
        {
            await NotifyAsync(_org, _bob, ActivityKinds.Mention, $"message {i}");
        }

        var first = await ListAsync(_org, _bob, limit: 2);
        var second = await ListAsync(_org, _bob, after: first.Next, limit: 2);
        var third = await ListAsync(_org, _bob, after: second.Next, limit: 2);

        first.Entries.Select(entry => entry.Preview).Should().Equal("message 4", "message 3");
        second.Entries.Select(entry => entry.Preview).Should().Equal("message 2", "message 1");
        third.Entries.Select(entry => entry.Preview).Should().Equal("message 0");
        third.Next.Should().BeNull();
    }

    [Fact]
    public async Task List_WhenThePageSizeOrCursorIsInvalid_ReturnsAValidationErrorAsync()
    {
        var results = await InOrganizationAsync(_org, async services =>
        {
            var bell = services.GetRequiredService<BellService>();
            return new[]
            {
                await bell.ListAsync(_bob, null, null, 0, CancellationToken),
                await bell.ListAsync(_bob, null, null, BellService.MaxPageSize + 1, CancellationToken),
                await bell.ListAsync(_bob, null, "not-a-cursor", null, CancellationToken),
                await bell.ListAsync(_bob, "other", null, null, CancellationToken),
            };
        });

        results.Select(result => result.Error?.Code).Should().Equal(
            "notifications.page_invalid", "notifications.page_invalid", "notifications.page_invalid", "notifications.tab_invalid");
    }

    [Fact]
    public async Task List_MentionsTab_ShowsOnlyMentionsAndRepliesToThePersonAsync()
    {
        await NotifyAsync(_org, _bob, ActivityKinds.DirectMessage, "plain");
        await NotifyAsync(_org, _bob, ActivityKinds.Mention, "mentioned");
        await NotifyAsync(_org, _bob, ActivityKinds.ThreadReply, "thread");

        var mentions = await ListAsync(_org, _bob, tab: "mentions");
        var all = await ListAsync(_org, _bob, tab: "all");

        mentions.Entries.Select(entry => entry.Preview).Should().Equal("mentioned");
        all.Entries.Should().HaveCount(3);
    }

    [Fact]
    public async Task MarkRead_SetsReadAtOnlyForThatEntryAsync()
    {
        await NotifyAsync(_org, _bob, ActivityKinds.Mention, "one");
        await NotifyAsync(_org, _bob, ActivityKinds.Mention, "two");
        var entries = (await ListAsync(_org, _bob)).Entries;

        var result = await InOrganizationAsync(_org, services => services.GetRequiredService<BellService>().MarkReadAsync(_bob, entries[0].Item.Id, CancellationToken));
        var after = (await ListAsync(_org, _bob)).Entries;

        result.IsSuccess.Should().BeTrue();
        after.Single(entry => entry.Item.Id == entries[0].Item.Id).Item.ReadAt.Should().NotBeNull();
        after.Single(entry => entry.Item.Id == entries[1].Item.Id).Item.ReadAt.Should().BeNull();
    }

    [Fact]
    public async Task MarkAllRead_MarksEveryUnreadEntryOfThePersonAsync()
    {
        var carol = Guid.CreateVersion7();
        await NotifyAsync(_org, _bob, ActivityKinds.Mention);
        await NotifyAsync(_org, _bob, ActivityKinds.Mention);
        await NotifyAsync(_org, carol, ActivityKinds.Mention);

        await InOrganizationAsync(_org, async services =>
        {
            await services.GetRequiredService<BellService>().MarkAllReadAsync(_bob, CancellationToken);
            return 0;
        });

        (await ListAsync(_org, _bob)).Entries.Should().OnlyContain(entry => entry.Item.ReadAt != null);
        (await ListAsync(_org, carol)).Entries.Should().OnlyContain(entry => entry.Item.ReadAt == null);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task List_WhenAccessWasLost_ShowsTheEntryWithoutPreviewAsync()
    {
        await NotifyAsync(_org, _bob, ActivityKinds.Mention, "private words");
        (await ListAsync(_org, _bob)).Entries.Single().Preview.Should().Be("private words");

        Access.Revoke(_conversation, _bob);
        var after = await ListAsync(_org, _bob);

        after.Entries.Should().ContainSingle().Which.Preview.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task BellRows_HoldIdsOnly_NeverTheMessageTextAsync()
    {
        await NotifyAsync(_org, _bob, ActivityKinds.Mention, "needle-in-the-text");

        var rowsWithText = await CountAsync("SELECT count(*) FROM notifications.inbox_items i WHERE i::text LIKE '%needle-in-the-text%'");

        rowsWithText.Should().Be(0);
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task List_OfAnotherAccountOrOrganization_ShowsNothingAsync()
    {
        var otherOrganization = Guid.CreateVersion7();
        var carol = Guid.CreateVersion7();
        await NotifyAsync(_org, _bob, ActivityKinds.Mention);

        (await ListAsync(_org, carol)).Entries.Should().BeEmpty();
        // The same account id in another organization: row-level security hides the rows even when the query names the account.
        (await ListAsync(otherOrganization, _bob)).Entries.Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task MarkRead_OfSomeoneElsesEntryOrFromAnotherOrganization_ReturnsNotFoundAsync()
    {
        var carol = Guid.CreateVersion7();
        var otherOrganization = Guid.CreateVersion7();
        await NotifyAsync(_org, _bob, ActivityKinds.Mention);
        var id = (await ListAsync(_org, _bob)).Entries.Single().Item.Id;

        var byCarol = await InOrganizationAsync(_org, services => services.GetRequiredService<BellService>().MarkReadAsync(carol, id, CancellationToken));
        var elsewhere = await InOrganizationAsync(otherOrganization, services => services.GetRequiredService<BellService>().MarkReadAsync(_bob, id, CancellationToken));

        byCarol.Error!.Code.Should().Be("notifications.not_found");
        elsewhere.Error!.Code.Should().Be("notifications.not_found");
        (await ListAsync(_org, _bob)).Entries.Single().Item.ReadAt.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Privacy")]
    public async Task Settings_OfAnotherOrganization_AreNotVisibleAsync()
    {
        await SaveAccountSettingsAsync(_org, _bob, "none");

        var inOther = await InOrganizationAsync(Guid.CreateVersion7(), services => services.GetRequiredService<Backroom.Modules.Notifications.Application.Settings.SettingsService>().GetAccountAsync(_bob, CancellationToken));
        var inOwn = await InOrganizationAsync(_org, services => services.GetRequiredService<Backroom.Modules.Notifications.Application.Settings.SettingsService>().GetAccountAsync(_bob, CancellationToken));

        inOther.DefaultLevel.Should().Be(Backroom.Modules.Notifications.Application.Settings.NotificationLevel.All);
        inOwn.DefaultLevel.Should().Be(Backroom.Modules.Notifications.Application.Settings.NotificationLevel.None);
    }
}
