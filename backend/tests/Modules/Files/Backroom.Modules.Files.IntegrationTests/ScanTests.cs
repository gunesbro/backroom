using System.Net;
using System.Security.Cryptography;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;
using Backroom.Modules.Files.Application.Scanning;
using Backroom.Modules.Files.Infrastructure.Scanning;
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
        await RunScanJobAsync(port: FreePort());

        (await StatusAsync(fileId)).Should().Be("scanning", "no answer is never read as clean");
        (await ScalarAsync("SELECT scan_attempts FROM files.files WHERE id = $1", fileId)).Should().Be(0, "an outage does not count against the file");
        (await ObjectSizeAsync(organizationId, fileId)).Should().NotBeNull();
        using var download = await DownloadAsync(organizationId, fileId, Alice);
        download.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task Scan_WhenTheFileIsInfected_TellsOnlyTheUploaderWithIdsAndAReasonAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        await AddMemberAsync(organizationId, Bob, SystemRoles.Member);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var fileId = await CompleteUnscannedAsync(organizationId, channelId, TestFiles.Text(ClamAvServer.Eicar), "text/plain", "Secret plan.txt");

        await RunScanJobAsync();

        var notice = Live.Rejected.Should().ContainSingle().Which;
        notice.OrgId.Should().Be(organizationId);
        notice.AccountId.Should().Be(await AccountAsync(Alice), "only the uploader is told, not Bob, who is in the same channel");
        notice.FileId.Should().Be(fileId);
        notice.Reason.Should().Be("malware");
        System.Text.Json.JsonSerializer.Serialize(notice).Should().NotContain("Secret plan").And.NotContain("EICAR");
    }

    [Fact]
    public async Task Scan_WhenTheFileIsClean_TellsNobodyAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        await CompleteUnscannedAsync(organizationId, channelId, TestFiles.Pdf());

        await RunScanJobAsync();

        Live.Rejected.Should().BeEmpty();
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
        Live.Rejected.Should().ContainSingle().Which.Reason.Should().Be("scan_failed");
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

    [Fact]
    public async Task Scan_WhenAFileCannotBeScanned_DoesNotBlockLaterFilesAndIsRejectedAfterFiveAttemptsAsync()
    {
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var stuck = await CompleteUnscannedAsync(organizationId, channelId, TestFiles.Text("STUCK and never answered"), "text/plain");
        var later = await CompleteUnscannedAsync(organizationId, channelId, TestFiles.Pdf());
        var realScanner = new ClamAvScanner(Microsoft.Extensions.Options.Options.Create(new ClamAvOptions { Host = ScannerHost, Port = ScannerPort }));

        await RunScanJobAsync(scannerOverride: new StuckScanner(realScanner));

        (await StatusAsync(later)).Should().Be("ready", "the file behind a stuck one is scanned");
        (await StatusAsync(stuck)).Should().Be("scanning");
        (await ScalarAsync("SELECT scan_attempts FROM files.files WHERE id = $1", stuck)).Should().Be(1);
        (await ScalarAsync("SELECT scan_next_at > now() + interval '4 minutes' FROM files.files WHERE id = $1", stuck)).Should().Be(true, "the claim backs the next attempt off");
        using var download = await DownloadAsync(organizationId, stuck, Alice);
        download.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // The fifth attempt has been made and is due again: the sixth claim gives up.
        await ExecuteAsync("UPDATE files.files SET scan_attempts = 5, scan_next_at = '-infinity' WHERE id = $1", stuck);
        await RunScanJobAsync(scannerOverride: new StuckScanner(realScanner));

        (await StatusAsync(stuck)).Should().Be("rejected");
        (await ObjectSizeAsync(organizationId, stuck)).Should().BeNull("the object of a file that was never scanned is deleted");
    }

    private sealed class StuckScanner(IFileScanner inner) : IFileScanner
    {
        public async Task<ScanVerdict> ScanAsync(Stream content, long length, CancellationToken cancellationToken)
        {
            var head = new byte[5];
            var read = await content.ReadAtLeastAsync(head, head.Length, throwOnEndOfStream: false, cancellationToken);
            if (read == 5 && head.AsSpan().SequenceEqual("STUCK"u8))
            {
                throw new ScannerUnavailableException("This file cannot be answered for.");
            }

            return await inner.ScanAsync(new PrefixedStream(head.AsMemory(0, read), content), length, cancellationToken);
        }
    }

    private sealed class PrefixedStream(ReadOnlyMemory<byte> prefix, Stream rest) : Stream
    {
        private int _taken;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_taken < prefix.Length)
            {
                var count = Math.Min(buffer.Length, prefix.Length - _taken);
                prefix.Slice(_taken, count).CopyTo(buffer);
                _taken += count;
                return count;
            }

            return await rest.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
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
