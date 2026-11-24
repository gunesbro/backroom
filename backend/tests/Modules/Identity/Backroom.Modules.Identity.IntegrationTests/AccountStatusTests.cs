using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Time.Testing;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Identity.IntegrationTests;

/// <summary>
/// The own account status (docs/contracts/phase-2.md, flow 9 and "Control plane"): <c>GET</c>/<c>PUT /api/v1/me/status</c>, its rules,
/// the fan-out as <c>AccountStatusChanged</c> to every cell where the account is a member (and no other), the rate limit and the flag.
/// The host runs on a fake clock in 2031 (other classes use other years); tokens use the real clock.
/// </summary>
[Trait("Category", "Privacy")]
public sealed class AccountStatusTests(PostgresDatabase database) : IAsyncLifetime
{
    private static readonly DateTimeOffset s_now = new(2031, 3, 1, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeTimeProvider _time = new(s_now);
    private readonly LogoutTokenIssuer _issuer = new();
    private readonly TestData _data = new(database);
    private WebApplication _controlPlane = null!;
    private HttpClient _client = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        _controlPlane = await IdentityUnderTest.StartAsync(
            database, CancellationToken, _time, _issuer.TrustedKey, settings: new Dictionary<string, string?> { ["Features:Presence"] = "true" });
        _client = _controlPlane.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        _client.Dispose();
        await _controlPlane.DisposeAsync();
        _issuer.Dispose();
    }

    [Fact]
    public async Task SetStatus_WithDndAndCustomStatus_IsStoredReturnedAndSentToEveryCellOfTheAccountAsync()
    {
        var account = Guid.NewGuid();
        var cell = await _data.AddCellAsync();
        var otherCell = await _data.AddCellAsync();
        var strangersCell = await _data.AddCellAsync();
        await _data.AddMembershipAsync(account, cell);
        await _data.AddMembershipAsync(account, otherCell);
        await _data.AddMembershipAsync(Guid.NewGuid(), strangersCell);
        var dnd = s_now.AddMinutes(30);
        var expires = s_now.AddHours(1);

        using var response = await PutAsync(account, new
        {
            dndUntil = dnd,
            customStatus = new { emoji = "🎯", text = "  Odaklanıyorum  ", expiresAt = expires },
            timeZone = "Europe/Istanbul",
        });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        var body = await JsonAsync(response);
        body["dndUntil"]!.GetValue<DateTimeOffset>().Should().Be(dnd);
        body["customStatus"]!["emoji"]!.GetValue<string>().Should().Be("🎯");
        body["customStatus"]!["text"]!.GetValue<string>().Should().Be("Odaklanıyorum", "the text is trimmed");
        body["timeZone"]!.GetValue<string>().Should().Be("Europe/Istanbul", "the owner sees their own zone");
        body["version"]!.GetValue<long>().Should().Be(1);

        foreach (var target in new[] { cell, otherCell })
        {
            var message = (await _data.AccountStatusMessagesAsync(target)).Should().ContainSingle().Subject;
            message.AccountId.Should().Be(account.ToString("D"));
            message.DndUntil.ToDateTimeOffset().Should().Be(dnd);
            message.CustomEmoji.Should().Be("🎯");
            message.CustomText.Should().Be("Odaklanıyorum");
            message.CustomExpiresAt.ToDateTimeOffset().Should().Be(expires);
            message.TimeZone.Should().Be("Europe/Istanbul");
            message.Version.Should().Be(1UL);
        }

        (await _data.AccountStatusMessagesAsync(strangersCell)).Should().BeEmpty("a cell where the account has no membership hears nothing");

        using var read = await GetAsync(account);
        var stored = await JsonAsync(read);
        stored["customStatus"]!["text"]!.GetValue<string>().Should().Be("Odaklanıyorum");
        stored["version"]!.GetValue<long>().Should().Be(1);
    }

    [Fact]
    public async Task SetStatus_Again_ReplacesTheWholeStatusWithANewerVersionAsync()
    {
        var account = Guid.NewGuid();
        var cell = await _data.AddCellAsync();
        await _data.AddMembershipAsync(account, cell);
        using (var first = await PutAsync(account, new { dndUntil = s_now.AddHours(1), customStatus = new { text = "Lunch" }, timeZone = "Europe/Istanbul" }))
        {
            first.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var second = await PutAsync(account, new { timeZone = "America/New_York" });

        var body = await JsonAsync(second);
        body["dndUntil"].Should().BeNull("a missing value clears it");
        body["customStatus"].Should().BeNull();
        body["version"]!.GetValue<long>().Should().Be(2);
        var messages = await _data.AccountStatusMessagesAsync(cell);
        messages.Select(message => message.Version).Should().Equal(1UL, 2UL);
        messages[1].CustomText.Should().BeEmpty();
        messages[1].DndUntil.Should().BeNull();
        messages[1].TimeZone.Should().Be("America/New_York");
    }

    [Fact]
    public async Task GetStatus_AfterTheEnds_LeavesExpiredPartsOutAsync()
    {
        var account = Guid.NewGuid();
        using (var set = await PutAsync(account, new
        {
            dndUntil = s_now.AddMinutes(30),
            customStatus = new { emoji = "🌴", expiresAt = s_now.AddHours(1) },
            timeZone = "Europe/Istanbul",
        }))
        {
            set.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        _time.Advance(TimeSpan.FromMinutes(31));
        var halfway = await JsonAsync(await GetAsync(account));
        halfway["dndUntil"].Should().BeNull();
        halfway["customStatus"]!["emoji"]!.GetValue<string>().Should().Be("🌴");

        _time.Advance(TimeSpan.FromMinutes(30));
        var after = await JsonAsync(await GetAsync(account));
        after["customStatus"].Should().BeNull();
        after["timeZone"]!.GetValue<string>().Should().Be("Europe/Istanbul");
    }

    [Fact]
    public async Task GetStatus_WhenNeverSet_IsEmptyWithVersionZeroAsync()
    {
        var body = await JsonAsync(await GetAsync(Guid.NewGuid()));

        body["dndUntil"].Should().BeNull();
        body["customStatus"].Should().BeNull();
        body["timeZone"].Should().BeNull();
        body["version"]!.GetValue<long>().Should().Be(0);
    }

    public static TheoryData<string, string> InvalidStatuses => new()
    {
        { "dnd in the past", """{"dndUntil":"2031-03-01T08:59:00Z","timeZone":"Europe/Istanbul"}""" },
        { "dnd beyond 7 days", """{"dndUntil":"2031-03-08T09:00:01Z","timeZone":"Europe/Istanbul"}""" },
        { "an empty custom status", """{"customStatus":{},"timeZone":"Europe/Istanbul"}""" },
        { "two emoji", """{"customStatus":{"emoji":"🎯🎯"},"timeZone":"Europe/Istanbul"}""" },
        { "a letter as emoji", """{"customStatus":{"emoji":"a"},"timeZone":"Europe/Istanbul"}""" },
        { "a digit as emoji", """{"customStatus":{"emoji":"1"},"timeZone":"Europe/Istanbul"}""" },
        { "a lone regional indicator", """{"customStatus":{"emoji":"🇹"},"timeZone":"Europe/Istanbul"}""" },
        { "the copyright sign", """{"customStatus":{"emoji":"©"},"timeZone":"Europe/Istanbul"}""" },
        { "box drawing", """{"customStatus":{"emoji":"─"},"timeZone":"Europe/Istanbul"}""" },
        { "braille", """{"customStatus":{"emoji":"⠿"},"timeZone":"Europe/Istanbul"}""" },
        { "a Hangul filler", """{"customStatus":{"text":"ㅤ"},"timeZone":"Europe/Istanbul"}""" },
        { "a Hangul choseong filler in text", """{"customStatus":{"text":"aᅟb"},"timeZone":"Europe/Istanbul"}""" },
        { "a halfwidth filler in text", """{"customStatus":{"text":"aﾠb"},"timeZone":"Europe/Istanbul"}""" },
        { "101 characters", $$"""{"customStatus":{"text":"{{new string('ı', 101)}}"},"timeZone":"Europe/Istanbul"}""" },
        { "a line break", """{"customStatus":{"text":"one\ntwo"},"timeZone":"Europe/Istanbul"}""" },
        { "a bidi override", """{"customStatus":{"text":"abc‮def"},"timeZone":"Europe/Istanbul"}""" },
        { "only spaces", """{"customStatus":{"text":"   "},"timeZone":"Europe/Istanbul"}""" },
        { "an end in the past", """{"customStatus":{"text":"x","expiresAt":"2031-03-01T08:00:00Z"},"timeZone":"Europe/Istanbul"}""" },
        { "an end beyond 30 days", """{"customStatus":{"text":"x","expiresAt":"2031-04-01T09:00:00Z"},"timeZone":"Europe/Istanbul"}""" },
    };

    [Theory]
    [MemberData(nameof(InvalidStatuses))]
    public async Task SetStatus_WhenARuleIsBroken_Answers400StatusInvalidAndSendsNothingAsync(string rule, string json)
    {
        var account = Guid.NewGuid();
        var cell = await _data.AddCellAsync();
        await _data.AddMembershipAsync(account, cell);

        using var response = await SendAsync(HttpMethod.Put, account, new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, rule);
        (await JsonAsync(response))["code"]!.GetValue<string>().Should().Be("identity.status_invalid");
        (await _data.AccountStatusMessagesAsync(cell)).Should().BeEmpty();
    }

    [Theory]
    [InlineData("""{"dndUntil":"2031-03-01T10:00:00Z"}""")]
    [InlineData("""{"timeZone":"Turkey Standard Time"}""")]
    [InlineData("""{"timeZone":"Mars/Olympus"}""")]
    public async Task SetStatus_WithAMissingOrUnknownZone_Answers400TimeZoneInvalidAsync(string json)
    {
        using var response = await SendAsync(HttpMethod.Put, Guid.NewGuid(), new StringContent(json, System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await JsonAsync(response))["code"]!.GetValue<string>().Should().Be("identity.time_zone_invalid", "a client falls back to UTC on this code only");
    }

    public static TheoryData<string> ValidEmoji => new()
    {
        "👍🏽",
        "👨‍👩‍👧‍👦",
        "🇹🇷",
        "1️⃣",
        "❤️",
        "🏴󠁧󠁢󠁳󠁣󠁴󠁿",
    };

    [Theory]
    [MemberData(nameof(ValidEmoji))]
    public async Task SetStatus_WithOneEmojiSequence_IsAcceptedAsync(string emoji)
    {
        using var response = await PutAsync(Guid.NewGuid(), new { customStatus = new { emoji }, timeZone = "UTC" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(response))["customStatus"]!["emoji"]!.GetValue<string>().Should().Be(emoji);
    }

    [Fact]
    public async Task SetStatus_WithOneHundredTurkishCharacters_IsAcceptedAsync()
    {
        var text = new string('İ', 50) + new string('ı', 50);

        using var response = await PutAsync(Guid.NewGuid(), new { customStatus = new { text }, timeZone = "Europe/Istanbul" });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        (await JsonAsync(response))["customStatus"]!["text"]!.GetValue<string>().Should().Be(text);
    }

    [Fact]
    public async Task SetStatus_MoreThan30TimesAMinute_Answers429RateLimitedAsync()
    {
        var account = Guid.NewGuid();
        for (var i = 0; i < 30; i++)
        {
            using var accepted = await PutAsync(account, new { timeZone = "UTC" });
            accepted.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        using var response = await PutAsync(account, new { timeZone = "UTC" });

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        response.Headers.RetryAfter.Should().NotBeNull();
        (await JsonAsync(response))["code"]!.GetValue<string>().Should().Be("identity.rate_limited");
        using var someoneElse = await PutAsync(Guid.NewGuid(), new { timeZone = "UTC" });
        someoneElse.StatusCode.Should().Be(HttpStatusCode.OK, "the limit is per account");
    }

    [Fact]
    public async Task Status_WithoutAToken_Answers401Async()
    {
        using var response = await _client.GetAsync(new Uri("/api/v1/me/status", UriKind.Relative), CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Status_WithTheFlagOff_IsNotMappedAsync()
    {
        await using var controlPlane = await IdentityUnderTest.StartAsync(database, CancellationToken, _time, _issuer.TrustedKey);
        using var client = controlPlane.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/api/v1/me/status", UriKind.Relative));
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _issuer.CreateAccessToken(Guid.NewGuid()));

        using var response = await client.SendAsync(request, CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    private Task<HttpResponseMessage> GetAsync(Guid account) => SendAsync(HttpMethod.Get, account, content: null);

    private Task<HttpResponseMessage> PutAsync(Guid account, object body) => SendAsync(HttpMethod.Put, account, JsonContent.Create(body));

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Guid account, HttpContent? content)
    {
        using var request = new HttpRequestMessage(method, new Uri("/api/v1/me/status", UriKind.Relative)) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _issuer.CreateAccessToken(account));
        return await _client.SendAsync(request, CancellationToken);
    }

    private static async Task<JsonNode> JsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync(CancellationToken))!;
}
