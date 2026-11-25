using AwesomeAssertions;
using Aspire.Hosting;
using Aspire.Hosting.ApplicationModel;
using Aspire.Hosting.Testing;

namespace Backroom.EndToEndTests;

/// <summary>
/// The development model's container images, read from the AppHost's model without building or starting it (no Docker,
/// so it runs in every test run, unlike the end-to-end tests).
/// </summary>
public sealed class ContainerImageTests
{
    [Fact]
    [Trait(TestCategory.Name, TestCategory.Short)]
    public async Task ContainerImages_InTheDevelopmentModel_AreValidPinnedReferencesAsync()
    {
        await using var builder = await DistributedApplicationTestingBuilder.CreateAsync<Projects.Backroom_AppHost>(
            [], (options, _) => options.DisableDashboard = true, TestContext.Current.CancellationToken);

        var images = builder.Resources
            .Select(resource => (resource.Name, Image: resource.Annotations.OfType<ContainerImageAnnotation>().LastOrDefault()))
            .Where(entry => entry.Image is not null)
            .ToList();

        images.Should().Contain(entry => entry.Name == "clamav");
        foreach (var (name, image) in images)
        {
            // Aspire writes the reference as image@sha256:{SHA256}; a value that already starts with "sha256:" gives
            // "@sha256:sha256:…", which Docker refuses ("invalid reference format"), so the container never starts.
            if (image!.SHA256 is not null)
            {
                image.SHA256.Should().MatchRegex("^[0-9a-f]{64}$", $"the digest of {name} has no prefix");
            }

            image.Tag.Should().NotBe("latest", $"{name} is pinned");
        }
    }
}
