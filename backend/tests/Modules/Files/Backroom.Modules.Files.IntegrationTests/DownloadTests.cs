using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Files.Application.Uploads;
using Backroom.Modules.Files.Infrastructure.Storage;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.ClamAv;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.SeaweedFs;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>
/// Signed downloads (docs/contracts/phase-2.md, flow 6, step 5; "Privacy rules": files through short-lived signed URLs,
/// access rechecked per request).
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class DownloadTests(PostgresDatabase database, KeycloakRealm keycloak, SeaweedFsServer storage, ClamAvServer scanner)
    : FilesTestBase(database, keycloak, storage, scanner)
{
    [Fact]
    public async Task Download_WhenReady_RedirectsToASixtySecondSignedUrlServingAnAttachmentAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var content = TestFiles.Png();
        var fileId = await UploadAsync(organizationId, channelId, Alice, content, name: "Gizli plan.png");

        using var response = await DownloadAsync(organizationId, fileId, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        response.Headers.GetValues("X-Content-Type-Options").Should().Equal("nosniff");
        response.Headers.GetValues("Referrer-Policy").Should().Equal("no-referrer");
        var location = response.Headers.Location!;
        location.AbsolutePath.Should().Be($"/{Bucket}/{organizationId}/{fileId}");
        Uri.UnescapeDataString(location.ToString()).Should().NotContain("Gizli");
        // The expiry is truncated to whole seconds and the SDK counts from its own clock when it signs, so the URL
        // lives 58 to 60 seconds; never longer than the contract's 60.
        location.Query.Should().MatchRegex("X-Amz-Expires=(58|59|60)&");

        using var stored = await StorageClient.GetAsync(location, CancellationToken);
        stored.StatusCode.Should().Be(HttpStatusCode.OK);
        stored.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
        stored.Content.Headers.ContentType!.MediaType.Should().Be("image/png");
        (await stored.Content.ReadAsByteArrayAsync(CancellationToken)).Should().Equal(content);
    }

    [Fact]
    public async Task SignedUrl_AfterItsLifetime_IsRefusedByStorageAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await UploadAsync(organizationId, channelId, Alice, TestFiles.Png());
        var storageAdapter = Services.GetRequiredService<S3FileStorage>();
        // Five seconds, not fewer: the SDK truncates the lifetime to whole seconds counted from its own clock when it
        // signs, and storage checks it against its own clock, so a two-second URL could be one second long or already
        // expired by the first request.
        var shortLived = await storageAdapter.CreateDownloadUrlAsync(
            FileService.StorageKey(organizationId, fileId), "image/png", DateTimeOffset.UtcNow.AddSeconds(5), CancellationToken);

        using (var valid = await StorageClient.GetAsync(shortLived, CancellationToken))
        {
            valid.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        await Task.Delay(TimeSpan.FromSeconds(7), CancellationToken);
        using var expired = await StorageClient.GetAsync(shortLived, CancellationToken);

        expired.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task SignedUrl_WhenTampered_IsRefusedByStorageAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await UploadAsync(organizationId, channelId, Alice, TestFiles.Png());
        var otherId = await UploadAsync(organizationId, channelId, Alice, TestFiles.Png());
        using var response = await DownloadAsync(organizationId, fileId, Alice);

        // The signature of one file used for another key.
        var tampered = new Uri(response.Headers.Location!.ToString().Replace(fileId.ToString(), otherId.ToString(), StringComparison.Ordinal));
        using var refused = await StorageClient.GetAsync(tampered, CancellationToken);
        // Without any signature.
        using var anonymous = await StorageClient.GetAsync(new Uri(Storage.Url, $"{Bucket}/{organizationId}/{fileId}"), CancellationToken);

        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        anonymous.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Download_WhenTheConversationBecomesUnreadable_IsRefusedOnTheNextRequestAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await UploadAsync(organizationId, channelId, Alice, TestFiles.Png());
        using (var allowed = await DownloadAsync(organizationId, fileId, Alice))
        {
            allowed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        // Archiving does not end read access: the file still downloads.
        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = $1", channelId);
        using (var archived = await DownloadAsync(organizationId, fileId, Alice))
        {
            archived.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        // Read access to this conversation ends for everyone (the way a removal from a private conversation ends it).
        await ExecuteAsync("UPDATE messaging.conversations SET kind = 'private_channel' WHERE id = $1", channelId);
        await ExecuteAsync("UPDATE messaging.conversation_members SET left_at = now() WHERE conversation_id = $1", channelId);
        // The same, still valid access token: access is checked again, not remembered.
        using var refused = await DownloadAsync(organizationId, fileId, Alice);

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(refused)).Should().Be("files.not_found");
        refused.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Download_WhenTheUploaderIsRemovedFromTheOrganization_IsRefusedOnTheNextRequestAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await JoinChannelAsync(organizationId, channelId, Bob);
        var fileId = await UploadAsync(organizationId, channelId, Bob, TestFiles.Png());
        using (var allowed = await DownloadAsync(organizationId, fileId, Bob))
        {
            allowed.StatusCode.Should().Be(HttpStatusCode.Redirect);
        }

        await ExecuteAsync("UPDATE organization.members SET status = 'removed' WHERE org_id = $1 AND account_id = $2", organizationId, await AccountAsync(Bob));
        using var refused = await DownloadAsync(organizationId, fileId, Bob);

        refused.StatusCode.Should().Be(HttpStatusCode.NotFound);
        refused.Headers.Location.Should().BeNull();
    }

    [Fact]
    public async Task Download_ByAnotherMemberOfAnUnattachedFile_IsRefusedEvenForTheOwnerAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        await JoinChannelAsync(organizationId, channelId, Bob);
        var fileId = await UploadAsync(organizationId, channelId, Bob, TestFiles.Png());

        // Alice owns the organization; no role reads files that no message she can read carries.
        using var response = await DownloadAsync(organizationId, fileId, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be("files.not_found");
    }

    [Fact]
    public async Task Download_WhenPendingOrUnknown_ReturnsTheSameNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var upload = await StartAsync(organizationId, channelId, Alice, "a.png", 600, "image/png");
        using (var put = await PutAsync(upload, TestFiles.Png()))
        {
            put.EnsureSuccessStatusCode();
        }

        using var pending = await DownloadAsync(organizationId, upload.FileId, Alice);
        using var unknown = await DownloadAsync(organizationId, Guid.CreateVersion7(), Alice);

        pending.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(pending)).Should().Be("files.not_found");
        (await ErrorCodeAsync(unknown)).Should().Be("files.not_found");
    }

    [Fact]
    public async Task Download_WithoutAToken_IsUnauthorizedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await UploadAsync(organizationId, channelId, Alice, TestFiles.Png());

        using var response = await DownloadAsync(organizationId, fileId, username: null!);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
