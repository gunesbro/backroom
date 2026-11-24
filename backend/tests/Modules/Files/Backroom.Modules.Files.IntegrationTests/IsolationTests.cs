using System.Net;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.Modules.Files.Application.Uploads;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.SeaweedFs;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>Organization isolation (docs/adr/0005): files of one organization are unreachable from another, by API and by row-level security.</summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class IsolationTests(PostgresDatabase database, KeycloakRealm keycloak, SeaweedFsServer storage)
    : FilesTestBase(database, keycloak, storage)
{
    private readonly PostgresDatabase _database = database;

    [Fact]
    public async Task Download_ThroughAnotherOrganizationOfTheSameAccount_ReturnsNotFoundAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(first, Alice);
        var fileId = await UploadAsync(first, channelId, Alice, TestFiles.Png());

        using var download = await DownloadAsync(second, fileId, Alice);
        using var complete = await CompleteAsync(second, fileId, Alice);

        download.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(download)).Should().Be("files.not_found");
        complete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task StartUpload_ToAConversationOfAnotherOrganization_ReturnsNotFoundAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        var otherChannel = await CreateChannelAsync(first, Alice);

        using var response = await StartUploadAsync(second, Alice, new { conversationId = otherChannel, name = "a.txt", size = 10, contentType = "text/plain" });

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ErrorCodeAsync(response)).Should().Be("files.conversation_not_found");
    }

    [Fact]
    public async Task FileStore_InAnotherOrganizationContext_SeesNoRowsEvenWhenTheQueryNamesTheOwnerAsync()
    {
        var first = await ProvisionForAsync(Alice);
        var second = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(first, Alice);
        var fileId = await UploadAsync(first, channelId, Alice, TestFiles.Png());

        FileRecord? found = null;
        await InOrganizationAsync(second, async services =>
            found = await services.GetRequiredService<IFileStore>().FindAsync(first, fileId, CancellationToken));

        found.Should().BeNull("row-level security limits the transaction to the entered organization");
    }

    [Fact]
    public async Task FilesTable_ForTheApplicationRoleWithoutAnOrganization_ReturnsNoRowsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        await UploadAsync(organizationId, channelId, Alice, TestFiles.Png());

        await using var connection = new NpgsqlConnection(_database.ApplicationConnectionString());
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM files.files", connection);
        var count = await command.ExecuteScalarAsync(CancellationToken);

        count.Should().Be(0L);
    }

    [Fact]
    public async Task FilesTables_HaveForcedRowLevelSecurityAsync()
    {
        var forced = await ScalarAsync(
            """
            SELECT bool_and(c.relrowsecurity AND c.relforcerowsecurity)
            FROM pg_class c JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE n.nspname = 'files' AND c.relkind = 'r' AND c.relname <> '__ef_migrations_history'
            """);

        forced.Should().Be(true);
    }
}
