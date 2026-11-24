using System.Net;
using AwesomeAssertions;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.SeaweedFs;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>Upload start and completion (docs/contracts/phase-2.md, flow 6, steps 1 to 3) against real SeaweedFS.</summary>
[Collection(CellCollection.Name)]
public sealed class UploadTests(PostgresDatabase database, KeycloakRealm keycloak, SeaweedFsServer storage)
    : FilesTestBase(database, keycloak, storage)
{
    [Fact]
    public async Task StartUpload_WhenValid_ReturnsAPresignedPutForAKeyWithoutTheNameAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);

        var upload = await StartAsync(organizationId, channelId, Alice, "Quarterly report.pdf", 300, "application/pdf");

        upload.Url.AbsolutePath.Should().Be($"/{Bucket}/{organizationId}/{upload.FileId}");
        Uri.UnescapeDataString(upload.Url.ToString()).Should().NotContain("Quarterly");
        upload.Url.Query.Should().Contain("X-Amz-Signature=").And.MatchRegex("X-Amz-Expires=(898|899|900)&"); // whole seconds, counted from the SDK's clock
        upload.Headers.Should().Contain("Content-Type", "application/pdf").And.Contain("If-None-Match", "*");
        upload.ExpiresAt.Should().BeCloseTo(DateTimeOffset.UtcNow.AddMinutes(15), TimeSpan.FromSeconds(30));
        var row = await ScalarAsync("SELECT status FROM files.files WHERE id = $1", upload.FileId);
        row.Should().Be("pending");
    }

    [Fact]
    public async Task UploadAndComplete_WhenContentMatches_MarksTheFileReadyWithTheDetectedTypeAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var content = TestFiles.Png();
        var upload = await StartAsync(organizationId, channelId, Alice, "İstanbul.png", content.Length, "IMAGE/PNG");

        using var put = await PutAsync(upload, content);
        using var completed = await CompleteAsync(organizationId, upload.FileId, Alice);

        put.StatusCode.Should().Be(HttpStatusCode.OK);
        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        var file = await ReadJsonAsync(completed);
        file["id"]!.GetValue<Guid>().Should().Be(upload.FileId);
        file["name"]!.GetValue<string>().Should().Be("İstanbul.png");
        file["size"]!.GetValue<long>().Should().Be(content.Length);
        file["contentType"]!.GetValue<string>().Should().Be("image/png");
        file["kind"]!.GetValue<string>().Should().Be("image");
        file["status"]!.GetValue<string>().Should().Be("ready");
    }

    [Fact]
    public async Task Complete_WhenRepeated_AnswersWithTheStoredFileAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await UploadAsync(organizationId, channelId, Alice, TestFiles.Png());

        using var repeat = await CompleteAsync(organizationId, fileId, Alice);

        repeat.StatusCode.Should().Be(HttpStatusCode.OK);
        (await ReadJsonAsync(repeat))["status"]!.GetValue<string>().Should().Be("ready");
    }

    [Fact]
    public async Task Complete_WhenOctetStreamIsARecognisedType_StoresTheRecognisedTypeAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var content = TestFiles.Pdf();
        var upload = await StartAsync(organizationId, channelId, Alice, "scan", content.Length, "application/octet-stream");
        using var put = await PutAsync(upload, content);

        using var completed = await CompleteAsync(organizationId, upload.FileId, Alice);

        var file = await ReadJsonAsync(completed);
        file["contentType"]!.GetValue<string>().Should().Be("application/pdf");
        file["kind"]!.GetValue<string>().Should().Be("document");
    }

    [Theory]
    [InlineData(104_857_601L, HttpStatusCode.RequestEntityTooLarge, "files.too_large")]
    [InlineData(10_000_000_000L, HttpStatusCode.RequestEntityTooLarge, "files.too_large")]
    [InlineData(0L, HttpStatusCode.BadRequest, "files.upload_invalid")]
    [InlineData(-5L, HttpStatusCode.BadRequest, "files.upload_invalid")]
    public async Task StartUpload_WhenSizeIsOutOfRange_IsRefusedAsync(long size, HttpStatusCode status, string code)
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);

        using var response = await StartUploadAsync(organizationId, Alice, new { conversationId = channelId, name = "big.zip", size, contentType = "application/zip" });

        response.StatusCode.Should().Be(status);
        (await ErrorCodeAsync(response)).Should().Be(code);
        (await ScalarAsync("SELECT count(*) FROM files.files WHERE org_id = $1", organizationId)).Should().Be(0L);
    }

    [Fact]
    public async Task StartUpload_WhenExactly100MB_IsAcceptedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);

        using var response = await StartUploadAsync(organizationId, Alice, new { conversationId = channelId, name = "big.zip", size = 104_857_600L, contentType = "application/zip" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/x-msdownload")]
    [InlineData("text/plain; charset=utf-8")]
    [InlineData("")]
    public async Task StartUpload_WhenTypeIsNotAllowed_IsRefusedAsync(string contentType)
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);

        using var response = await StartUploadAsync(organizationId, Alice, new { conversationId = channelId, name = "page", size = 10, contentType });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("files.type_not_allowed");
    }

    [Theory]
    [InlineData("../etc/passwd")]
    [InlineData("folder\\file.txt")]
    [InlineData("report‮txt.exe")]
    [InlineData("line\nbreak.txt")]
    [InlineData("  ")]
    [InlineData("..")]
    public async Task StartUpload_WhenNameIsInvalid_IsRefusedAsync(string name)
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);

        using var response = await StartUploadAsync(organizationId, Alice, new { conversationId = channelId, name, size = 10, contentType = "text/plain" });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(response)).Should().Be("files.name_invalid");
    }

    [Fact]
    public async Task StartUpload_WhenNameIsLongerThan255Characters_IsRefusedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);

        using var accepted = await StartUploadAsync(organizationId, Alice, new { conversationId = channelId, name = new string('ş', 255), size = 10, contentType = "text/plain" });
        using var refused = await StartUploadAsync(organizationId, Alice, new { conversationId = channelId, name = new string('ş', 256), size = 10, contentType = "text/plain" });

        accepted.StatusCode.Should().Be(HttpStatusCode.Created);
        refused.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task StartUpload_WhenConversationIsUnknownOrArchived_ReturnsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var archived = await CreateChannelAsync(organizationId, Alice);
        await ExecuteAsync("UPDATE messaging.conversations SET archived_at = now() WHERE id = $1", archived);

        using var unknown = await StartUploadAsync(organizationId, Alice, new { conversationId = Guid.CreateVersion7(), name = "a.txt", size = 10, contentType = "text/plain" });
        using var closed = await StartUploadAsync(organizationId, Alice, new { conversationId = archived, name = "a.txt", size = 10, contentType = "text/plain" });

        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(unknown)).Should().Be("files.conversation_not_found");
        closed.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StartUpload_WithoutFilesUpload_IsForbiddenAndOutsidersGetNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        await AddMemberAsync(organizationId, Bob, role: null);
        var body = new { conversationId = channelId, name = "a.txt", size = 10, contentType = "text/plain" };

        using var withoutPermission = await StartUploadAsync(organizationId, Bob, body);
        using var outsider = await StartUploadAsync(await ProvisionForAsync(Alice), Bob, body);

        withoutPermission.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        outsider.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(outsider)).Should().Be("organization.not_found");
    }

    [Fact]
    public async Task StartUpload_OverTheDailyLimit_Answers429WithRetryAfterAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        await ExecuteAsync(
            "INSERT INTO files.account_upload_days (org_id, account_id, day, bytes) VALUES ($1, $2, $3, $4)",
            organizationId, await AccountAsync(Alice), DateOnly.FromDateTime(DateTime.UtcNow), 10L * 1024 * 1024 * 1024);

        using var response = await StartUploadAsync(organizationId, Alice, new { conversationId = channelId, name = "a.txt", size = 10, contentType = "text/plain" });

        response.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await ErrorCodeAsync(response)).Should().Be("files.rate_limited");
        response.Headers.RetryAfter!.Delta.Should().BePositive().And.BeLessThanOrEqualTo(TimeSpan.FromDays(1));
    }

    [Fact]
    public async Task StartUpload_AsAMember_IsAllowedByTheMemberRoleAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);

        using var response = await StartUploadAsync(organizationId, Bob, new { conversationId = channelId, name = "a.txt", size = 10, contentType = "text/plain" });

        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Complete_WhenNothingWasUploaded_ReturnsUploadIncompleteAndStaysPendingAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var upload = await StartAsync(organizationId, channelId, Alice, "a.png", 600, "image/png");

        using var response = await CompleteAsync(organizationId, upload.FileId, Alice);

        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(response)).Should().Be("files.upload_incomplete");
        (await ScalarAsync("SELECT status FROM files.files WHERE id = $1", upload.FileId)).Should().Be("pending");
    }

    [Fact]
    public async Task Complete_WhenStoredSizeDiffersFromTheDeclaredSize_RefusesAndDeletesTheObjectAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var upload = await StartAsync(organizationId, channelId, Alice, "a.png", 600, "image/png");
        // Storage refuses another size through the signed URL, so a store that did not would be the way in: write it directly.
        await PutObjectDirectlyAsync(organizationId, upload.FileId, TestFiles.Png(400));

        using var refused = await CompleteAsync(organizationId, upload.FileId, Alice);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(refused)).Should().Be("files.upload_incomplete");
        (await ScalarAsync("SELECT status FROM files.files WHERE id = $1", upload.FileId)).Should().Be("pending");
        // The object is gone, so the right content can still be uploaded with the same URL and completed.
        using var retry = await PutAsync(upload, TestFiles.Png(600));
        retry.StatusCode.Should().Be(HttpStatusCode.OK);
        using var completed = await CompleteAsync(organizationId, upload.FileId, Alice);
        completed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Theory]
    [InlineData("image/png", "text")]
    [InlineData("application/pdf", "png")]
    [InlineData("text/plain", "png")]
    [InlineData("text/csv", "executable")]
    [InlineData("image/jpeg", "png")]
    public async Task Complete_WhenContentDoesNotMatchTheDeclaredType_RefusesAndDeletesTheObjectAsync(string declared, string actual)
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var content = actual switch
        {
            "text" => TestFiles.Text(new string('x', 300)),
            "png" => TestFiles.Png(300),
            _ => TestFiles.WindowsExecutable(300),
        };
        var upload = await StartAsync(organizationId, channelId, Alice, "file", content.Length, declared);
        using (var put = await PutAsync(upload, content))
        {
            put.EnsureSuccessStatusCode();
        }

        using var refused = await CompleteAsync(organizationId, upload.FileId, Alice);
        using var again = await CompleteAsync(organizationId, upload.FileId, Alice);

        refused.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(refused)).Should().Be("files.type_mismatch");
        (await ErrorCodeAsync(again)).Should().Be("files.upload_incomplete", "the refused object was deleted");
    }

    [Fact]
    public async Task Complete_ByAnotherAccount_ReturnsNotFoundAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Admin);
        var content = TestFiles.Png();
        var upload = await StartAsync(organizationId, channelId, Alice, "a.png", content.Length, "image/png");
        using var put = await PutAsync(upload, content);

        using var response = await CompleteAsync(organizationId, upload.FileId, Bob);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be("files.not_found");
        (await ScalarAsync("SELECT status FROM files.files WHERE id = $1", upload.FileId)).Should().Be("pending");
    }

    [Fact]
    public async Task Upload_WhenTheKeyAlreadyHoldsAnObject_IsRefusedByStorageAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var content = TestFiles.Png();
        var upload = await StartAsync(organizationId, channelId, Alice, "a.png", content.Length, "image/png");
        using (var first = await PutAsync(upload, content))
        {
            first.EnsureSuccessStatusCode();
        }

        using var completed = await CompleteAsync(organizationId, upload.FileId, Alice);
        // The same URL, still valid, must not replace the checked object with other content.
        using var replace = await PutAsync(upload, TestFiles.WindowsExecutable(content.Length));

        completed.StatusCode.Should().Be(HttpStatusCode.OK);
        replace.StatusCode.Should().Be(HttpStatusCode.PreconditionFailed);
    }

    [Theory]
    [InlineData(400)]
    [InlineData(601)]
    public async Task Upload_WithAnotherSizeThanDeclared_IsRefusedByStorageAsync(int size)
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var upload = await StartAsync(organizationId, channelId, Alice, "a.png", 600, "image/png");

        using var put = await PutAsync(upload, TestFiles.Png(size));

        put.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        upload.Url.Query.Should().Contain("X-Amz-SignedHeaders=content-length%3Bcontent-type%3Bhost%3Bif-none-match");
    }

    [Fact]
    public async Task Upload_WithoutTheSignedHeaders_IsRefusedByStorageAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var content = TestFiles.Png();
        var upload = await StartAsync(organizationId, channelId, Alice, "a.png", content.Length, "image/png");

        using var otherType = await PutAsync(upload, content, new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = "text/html", ["If-None-Match"] = "*" });
        using var withoutCondition = await PutAsync(upload, content, new Dictionary<string, string>(StringComparer.Ordinal) { ["Content-Type"] = "image/png" });

        otherType.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        withoutCondition.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
