using System.Text.Json;
using AwesomeAssertions;

namespace Backroom.ArchitectureTests;

public sealed class ObjectStorageUsageTests
{
    [Fact]
    public void CompiledAssemblies_WhenOutsideFilesInfrastructure_DoNotReferenceObjectStorageSdks()
    {
        var violations = SolutionAssemblies.All
            .SelectMany(assembly => ObjectStorageUsageRules.FindViolations(
                assembly.GetName().Name!,
                assembly.GetReferencedAssemblies().Select(reference => reference.Name!)))
            .ToList();

        violations.Should().BeEmpty();
    }

    [Fact]
    public void CompiledAssemblies_FilesInfrastructure_IsLoadedAndUsesTheS3Sdk()
    {
        // Guards the test above against passing because the module was not loaded.
        var files = SolutionAssemblies.Get(ObjectStorageUsageRules.FilesInfrastructure);

        files.GetReferencedAssemblies().Should().Contain(reference => reference.Name == "AWSSDK.S3");
    }

    [Fact]
    public void NuGetPackages_WhenOutsideFilesInfrastructure_DoNotIncludeObjectStorageSdks()
    {
        var violations = new List<string>();
        foreach (var lockFile in Directory.EnumerateFiles(RepositoryPaths.SourceDirectory, "packages.lock.json", SearchOption.AllDirectories))
        {
            var project = Path.GetFileName(Path.GetDirectoryName(lockFile)!);
            using var document = JsonDocument.Parse(File.ReadAllText(lockFile));
            var direct = document.RootElement.GetProperty("dependencies").EnumerateObject()
                .SelectMany(framework => framework.Value.EnumerateObject())
                .Where(package => package.Value.TryGetProperty("type", out var type)
                    && string.Equals(type.GetString(), "Direct", StringComparison.Ordinal))
                .Select(package => package.Name);

            violations.AddRange(ObjectStorageUsageRules.FindViolations(project, direct));
        }

        violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "AWSSDK.S3")]
    [InlineData("Backroom.Modules.Files.Application", "AWSSDK.S3")]
    [InlineData("Backroom.Cell.Host", "Minio")]
    [InlineData("Backroom.Modules.Organization.Infrastructure", "Azure.Storage.Blobs")]
    public void FindViolations_WhenOutsideFilesInfrastructureUsesAStorageSdk_ReturnsViolation(string assembly, string reference)
    {
        ObjectStorageUsageRules.FindViolations(assembly, [reference]).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Backroom.Modules.Files.Infrastructure", "AWSSDK.S3")]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "AWSSDK.Core")]
    [InlineData("Backroom.Modules.Email.Infrastructure", "MailKit")]
    public void FindViolations_WhenAllowed_ReturnsNothing(string assembly, string reference)
    {
        ObjectStorageUsageRules.FindViolations(assembly, [reference]).Should().BeEmpty();
    }
}
