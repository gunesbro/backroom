using System.Text.Json;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using AwesomeAssertions;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Backroom.ArchitectureTests;

public sealed class SmtpUsageTests
{
    private static readonly System.Reflection.Assembly[] s_nonEmailAssemblies = SolutionAssemblies.All
        .Where(assembly => !SmtpUsageRules.MayUseSmtp(assembly.GetName().Name!))
        .ToArray();

    private static readonly Architecture s_architecture = new ArchLoader().LoadAssemblies(s_nonEmailAssemblies).Build();

    [Fact]
    public void CompiledAssemblies_WhenOutsideEmailInfrastructure_DoNotReferenceMailLibraries()
    {
        var violations = SolutionAssemblies.All
            .SelectMany(assembly => SmtpUsageRules.FindViolations(
                assembly.GetName().Name!,
                assembly.GetReferencedAssemblies().Select(reference => reference.Name!)))
            .ToList();

        violations.Should().BeEmpty();
    }

    [Fact]
    public void Types_WhenOutsideEmailInfrastructure_DoNotDependOnMailTypes()
    {
        Types().That().ResideInAssembly(s_nonEmailAssemblies[0], s_nonEmailAssemblies[1..])
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(SmtpUsageRules.MailNamespacePattern)
            .WithoutRequiringPositiveResults()
            .Check(s_architecture);
    }

    [Fact]
    public void NuGetPackages_WhenOutsideEmailInfrastructure_DoNotIncludeMailLibraries()
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

            violations.AddRange(SmtpUsageRules.FindViolations(project, direct));
        }

        violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "System.Net.Mail")]
    [InlineData("Backroom.Cell.Host", "MailKit")]
    [InlineData("Backroom.BuildingBlocks.Infrastructure", "MimeKit")]
    [InlineData("Backroom.Modules.Email.Contracts", "MailKit")]
    [InlineData("Backroom.Modules.Organization.Infrastructure", "SendGrid")]
    [InlineData("Backroom.Modules.Organization.Infrastructure", "AWSSDK.SimpleEmailV2")]
    public void FindViolations_WhenOutsideEmailInfrastructureUsesMailLibrary_ReturnsViolation(string assembly, string reference)
    {
        SmtpUsageRules.FindViolations(assembly, [reference]).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Backroom.Modules.Email.Infrastructure", "MailKit")]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "System.Net.Http")]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "Mailpit.Fake")]
    public void FindViolations_WhenAllowed_ReturnsNothing(string assembly, string reference)
    {
        SmtpUsageRules.FindViolations(assembly, [reference]).Should().BeEmpty();
    }
}
