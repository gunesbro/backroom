using AwesomeAssertions;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;
using Backroom.Testing.SeaweedFs;

namespace Backroom.Modules.Files.IntegrationTests;

/// <summary>"No content in logs, traces or events" (docs/contracts/phase-2.md, "Privacy rules"): file names, content and signed URLs never reach a log.</summary>
[Collection(CellCollection.Name)]
[Trait("Category", "Privacy")]
public sealed class LogPrivacyTests(PostgresDatabase database, KeycloakRealm keycloak, SeaweedFsServer storage)
    : FilesTestBase(database, keycloak, storage)
{
    [Fact]
    public async Task UploadCompleteAndDownload_WhenLoggedAtTrace_NeverLogNamesContentOrSignaturesAsync()
    {
        const string Name = "Maaş-bordrosu-İzmir-7f3a.pdf";
        const string Marker = "secret-content-91c2";
        var organizationId = await ProvisionForAsync(Alice);
        var channelId = await CreateChannelAsync(organizationId, Alice);
        var content = TestFiles.Pdf(300);
        System.Text.Encoding.ASCII.GetBytes(Marker).CopyTo(content, 20);

        var fileId = await UploadAsync(organizationId, channelId, Alice, content, "application/pdf", Name);
        var mismatch = await StartAsync(organizationId, channelId, Alice, Name, 300, "image/png");
        using (var put = await PutAsync(mismatch, content))
        {
            put.EnsureSuccessStatusCode();
        }

        using var refused = await CompleteAsync(organizationId, mismatch.FileId, Alice);
        using var download = await DownloadAsync(organizationId, fileId, Alice);

        var entries = Logs.Entries;
        entries.Should().Contain(entry => entry.Contains(fileId.ToString(), StringComparison.Ordinal), "the module logs ids");
        entries.Should().NotContain(entry => entry.Contains("Maaş", StringComparison.Ordinal) || entry.Contains("7f3a", StringComparison.Ordinal));
        entries.Should().NotContain(entry => entry.Contains(Marker, StringComparison.Ordinal));
        entries.Should().NotContain(entry => entry.Contains("X-Amz-Signature", StringComparison.OrdinalIgnoreCase));
        entries.Should().NotContain(entry => entry.Contains(Storage.SecretAccessKey, StringComparison.Ordinal));
    }
}
