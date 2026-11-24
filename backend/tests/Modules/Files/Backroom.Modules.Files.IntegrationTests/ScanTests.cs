using System.Net;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Files.Application.Scanning;
using Backroom.Modules.Permissions.Contracts;
using Backroom.Modules.Files.Application.Storage;
using Backroom.Testing.ClamAv;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.SeaweedFs;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>
/// The malware scan (docs/contracts/phase-2.md, flow 6, step 3) with the real ClamAV container (same image as
/// development, offline, EICAR signature only) and the scan job running as the maintenance role.
/// </summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class ScanTests(PostgresDatabase database, KeycloakRealm keycloak, SeaweedFsServer storage, ClamAvServer scanner)
    : FilesTestBase(database, keycloak, storage, scanner)
{
    [Fact]
    public async Task Download_WhileTheScanHasNotRun_AnswersScanPendingToTheUploaderAndNotFoundToOthersAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await CompleteUnscannedAsync(organizationId, channelId, TestFiles.Pdf());

        using var own = await DownloadAsync(organizationId, fileId, Alice);
        using var other = await DownloadAsync(organizationId, fileId, Bob);

        own.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ErrorCodeAsync(own)).Should().Be("files.scan_pending");
        other.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StatusAsync(fileId)).Should().Be("scanning");
    }

    [Fact]
    public async Task Scan_WhenTheFileIsClean_MakesItReadyAndDownloadableAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await CompleteUnscannedAsync(organizationId, channelId, TestFiles.Pdf());

        await RunScanJobAsync();

        (await StatusAsync(fileId)).Should().Be("ready");
        (await ScalarAsync("SELECT ready_at IS NOT NULL FROM files.files WHERE id = $1", fileId)).Should().Be(true);
        using var download = await DownloadAsync(organizationId, fileId, Alice);
        download.StatusCode.Should().Be(HttpStatusCode.Redirect);
    }

    [Fact]
    public async Task Scan_WhenTheFileIsInfected_RejectsItDeletesTheObjectAndRefusesDownloadsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await CompleteUnscannedAsync(organizationId, channelId, TestFiles.Text(ClamAvServer.Eicar), "text/plain");

        await RunScanJobAsync();

        (await StatusAsync(fileId)).Should().Be("rejected");
        (await ObjectSizeAsync(organizationId, fileId)).Should().BeNull("an infected object is deleted");
        using var download = await DownloadAsync(organizationId, fileId, Alice);
        download.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Scan_WhenTheScannerIsUnavailable_KeepsTheFileUnscannedAndUnservedAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await CompleteUnscannedAsync(organizationId, channelId, TestFiles.Text(ClamAvServer.Eicar), "text/plain");

        // Nothing listens on the port, so the connection is refused.
        var act = async () => await RunScanJobAsync(port: FreePort());

        await act.Should().ThrowAsync<ScannerUnavailableException>();
        (await StatusAsync(fileId)).Should().Be("scanning", "no answer is never read as clean");
        (await ObjectSizeAsync(organizationId, fileId)).Should().NotBeNull();
        using var download = await DownloadAsync(organizationId, fileId, Alice);
        download.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Scan_WhenTheObjectIsMissing_RejectsTheFileAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await CompleteUnscannedAsync(organizationId, channelId, TestFiles.Pdf());
        await Services.GetRequiredService<IFileStorageProvider>().ForOrganization(organizationId).DeleteAsync($"{organizationId}/{fileId}", CancellationToken);

        await RunScanJobAsync();

        (await StatusAsync(fileId)).Should().Be("rejected");
    }

    [Fact]
    public async Task Scan_WhenTheFileIsLarge_StreamsItToTheScannerAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        // Past clamd's default 25 MB stream limit: the daemon is configured for 100 MB files.
        var content = RandomNumberGenerator.GetBytes(30 * 1024 * 1024);
        var fileId = await CompleteUnscannedAsync(organizationId, channelId, content, "application/octet-stream", "big.bin");

        await RunScanJobAsync();

        (await StatusAsync(fileId)).Should().Be("ready");
    }

    [Fact]
    public async Task Scan_WhenAFileIsScanned_LogsNoNameOrContentAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        await CompleteUnscannedAsync(organizationId, channelId, TestFiles.Text(ClamAvServer.Eicar), "text/plain", "Secret plan.txt");

        await RunScanJobAsync();

        string.Join(Environment.NewLine, Logs.Entries).Should().NotContain("Secret plan").And.NotContain("EICAR");
    }

    private static int FreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    /// <summary>Starts, uploads and completes a file, leaving it in <c>scanning</c>.</summary>
    private async Task<Guid> CompleteUnscannedAsync(Guid organizationId, Guid channelId, byte[] content, string contentType = "application/pdf", string name = "plan.pdf")
    {
        var upload = await StartAsync(organizationId, channelId, Alice, name, content.Length, contentType);
        using (var put = await PutAsync(upload, content))
        {
            put.EnsureSuccessStatusCode();
        }

        using var completed = await CompleteAsync(organizationId, upload.FileId, Alice);
        completed.EnsureSuccessStatusCode();
        (await ReadJsonAsync(completed))["status"]!.GetValue<string>().Should().Be("scanning");
        return upload.FileId;
    }

    private async Task<string?> StatusAsync(Guid fileId) =>
        (string?)await ScalarAsync("SELECT status FROM files.files WHERE id = $1", fileId);

    private Task<long?> ObjectSizeAsync(Guid organizationId, Guid fileId) =>
        Services.GetRequiredService<IFileStorageProvider>().ForOrganization(organizationId).GetSizeAsync($"{organizationId}/{fileId}", CancellationToken);
}
