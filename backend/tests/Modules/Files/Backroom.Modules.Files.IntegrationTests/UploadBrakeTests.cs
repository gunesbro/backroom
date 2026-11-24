using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using Backroom.BuildingBlocks.Application.Organizations;
using Backroom.BuildingBlocks.Application.Persistence;
using Backroom.BuildingBlocks.Domain.Errors;
using Backroom.Modules.Files.Application.Uploads;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.ClamAv;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.SeaweedFs;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>
/// The abuse brakes of flow 6 (30 starts per minute and 10 GB per day per account, 100 GB per day per organization) on a
/// fake clock. Starts run through the service in a unit of work that, like the endpoint, commits only on success.
/// </summary>
[Collection(CellCollection.Name)]
public sealed class UploadBrakeTests(PostgresDatabase database, KeycloakRealm keycloak, SeaweedFsServer storage, ClamAvServer scanner)
    : FilesTestBase(database, keycloak, storage, scanner)
{
    private const long Megabyte = 1024 * 1024;

    // Starts at the real time, so the tokens the setup requests are valid; moved forward only after them.
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.UtcNow);

    protected override void ConfigureServices(IServiceCollection services) => services.AddSingleton<TimeProvider>(_clock);

    [Fact]
    public async Task StartUpload_AfterThirtyStartsInAMinute_IsRateLimitedUntilTheOldestLeavesTheWindowAsync()
    {
        var (organizationId, channelId, alice) = await SetUpAsync();
        for (var i = 0; i < UploadBrakes.StartsPerMinute; i++)
        {
            (await StartThroughServiceAsync(organizationId, channelId, alice, 10)).IsSuccess.Should().BeTrue();
            _clock.Advance(TimeSpan.FromSeconds(1));
        }

        var refused = await StartThroughServiceAsync(organizationId, channelId, alice, 10);

        refused.IsSuccess.Should().BeFalse();
        refused.Error!.Code.Should().Be("files.rate_limited");
        refused.Error!.Kind.Should().Be(ErrorKind.RateLimited);
        refused.Error!.RetryAfter.Should().BeCloseTo(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(1));
        (await ScalarAsync("SELECT count(*) FROM files.files WHERE org_id = $1", organizationId)).Should().Be(30L, "a refused start writes nothing");

        _clock.Advance(refused.Error!.RetryAfter!.Value);
        (await StartThroughServiceAsync(organizationId, channelId, alice, 10)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task StartUpload_OverTenGigabytesInADayForTheAccount_IsRateLimitedUntilTheNextDayAsync()
    {
        var (organizationId, channelId, alice) = await SetUpAsync();
        await ExecuteAsync(
            "INSERT INTO files.account_upload_days (org_id, account_id, day, bytes) VALUES ($1, $2, $3, $4)",
            organizationId, alice, Today, UploadBrakes.AccountBytesPerDay - (50 * Megabyte));

        var refused = await StartThroughServiceAsync(organizationId, channelId, alice, 100 * Megabyte);
        var exactlyAtTheLimit = await StartThroughServiceAsync(organizationId, channelId, alice, 50 * Megabyte);
        var overByOne = await StartThroughServiceAsync(organizationId, channelId, alice, 1);

        refused.Error!.Code.Should().Be("files.rate_limited");
        refused.Error!.RetryAfter.Should().Be(TimeUntilTomorrow);
        exactlyAtTheLimit.IsSuccess.Should().BeTrue();
        overByOne.IsSuccess.Should().BeFalse();

        _clock.Advance(TimeUntilTomorrow);
        (await StartThroughServiceAsync(organizationId, channelId, alice, 100 * Megabyte)).IsSuccess.Should().BeTrue();
    }

    [Fact]
    public async Task StartUpload_OverOneHundredGigabytesInADayForTheOrganization_IsRateLimitedForEveryMemberAsync()
    {
        var (organizationId, channelId, alice) = await SetUpAsync();
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var bob = await AccountAsync(Bob);
        await ExecuteAsync(
            "INSERT INTO files.upload_days (org_id, day, bytes) VALUES ($1, $2, $3)",
            organizationId, Today, UploadBrakes.OrganizationBytesPerDay - 10);

        var small = await StartThroughServiceAsync(organizationId, channelId, alice, 10);
        var aliceOver = await StartThroughServiceAsync(organizationId, channelId, alice, 1);
        var bobOver = await StartThroughServiceAsync(organizationId, channelId, bob, 1);

        small.IsSuccess.Should().BeTrue();
        aliceOver.Error!.Code.Should().Be("files.rate_limited");
        bobOver.Error!.Code.Should().Be("files.rate_limited");
        bobOver.Error!.RetryAfter.Should().Be(TimeUntilTomorrow);
        (await ScalarAsync("SELECT bytes FROM files.account_upload_days WHERE org_id = $1 AND account_id = $2", organizationId, bob))
            .Should().BeNull("the refused start's count was rolled back");
    }

    [Fact]
    public async Task StartUpload_InAnotherOrganization_HasItsOwnCountersAsync()
    {
        var (organizationId, channelId, alice) = await SetUpAsync();
        var other = await ProvisionForAsync(Alice);
        var otherChannel = await CreateChannelAsync(other, Alice);
        await ExecuteAsync(
            "INSERT INTO files.upload_days (org_id, day, bytes) VALUES ($1, $2, $3)",
            organizationId, Today, UploadBrakes.OrganizationBytesPerDay);

        (await StartThroughServiceAsync(organizationId, channelId, alice, 1)).IsSuccess.Should().BeFalse();
        (await StartThroughServiceAsync(other, otherChannel, alice, 1)).IsSuccess.Should().BeTrue();
    }

    private DateOnly Today => DateOnly.FromDateTime(_clock.GetUtcNow().UtcDateTime);

    private TimeSpan TimeUntilTomorrow
    {
        get
        {
            var now = _clock.GetUtcNow();
            var tomorrow = new DateTimeOffset(Today.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
            // Retry-After is stored to microseconds like every stored time; compare on the same clock reading.
            return tomorrow - new DateTimeOffset(now.UtcTicks - (now.UtcTicks % 10), TimeSpan.Zero);
        }
    }

    private async Task<(Guid OrganizationId, Guid ChannelId, Guid Alice)> SetUpAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        return (organizationId, channelId, await AccountAsync(Alice));
    }

    private async Task<Result<Backroom.Modules.Files.Application.Uploads.StartedUpload>> StartThroughServiceAsync(Guid organizationId, Guid channelId, Guid accountId, long size)
    {
        await using var scope = Services.CreateAsyncScope();
        scope.ServiceProvider.GetRequiredService<OrganizationContext>().Enter(organizationId);
        var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
        await unitOfWork.BeginAsync(CancellationToken);
        var result = await scope.ServiceProvider.GetRequiredService<FileService>()
            .StartAsync(accountId, channelId, "a.txt", size, "text/plain", CancellationToken);
        if (result.IsSuccess)
        {
            await unitOfWork.CommitAsync(CancellationToken);
        }
        else
        {
            await unitOfWork.RollbackAsync(CancellationToken);
        }

        return result;
    }
}
