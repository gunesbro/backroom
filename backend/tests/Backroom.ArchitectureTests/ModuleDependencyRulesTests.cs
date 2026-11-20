using AwesomeAssertions;

namespace Backroom.ArchitectureTests;

/// <summary>
/// Tests of the rule engine itself, with synthetic project names, so the rules are proven before modules exist.
/// </summary>
public sealed class ModuleDependencyRulesTests
{
    [Theory]
    [InlineData("Backroom.Modules.Messaging.Application", "Backroom.Modules.Messaging.Domain")]
    [InlineData("Backroom.Modules.Messaging.Application", "Backroom.Modules.Messaging.Contracts")]
    [InlineData("Backroom.Modules.Messaging.Application", "Backroom.Modules.Files.Contracts")]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "Backroom.Modules.Messaging.Application")]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "Backroom.Modules.Files.Contracts")]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "Backroom.BuildingBlocks.Infrastructure")]
    [InlineData("Backroom.Modules.Messaging.Presentation", "Backroom.Modules.Messaging.Application")]
    [InlineData("Backroom.Modules.Messaging.Domain", "Backroom.BuildingBlocks.Domain")]
    [InlineData("Backroom.Modules.Messaging.Contracts", "Backroom.BuildingBlocks.Domain")]
    [InlineData("Backroom.Modules.Messaging.Domain", "System.Runtime")]
    [InlineData("Backroom.AppHost", "Backroom.Cell.Host")]
    [InlineData("Backroom.BuildingBlocks.Infrastructure", "Backroom.BuildingBlocks.Application")]
    [InlineData("Backroom.BuildingBlocks.Presentation", "Backroom.BuildingBlocks.Domain")]
    [InlineData("Backroom.Modules.Messaging.Presentation", "Backroom.BuildingBlocks.Presentation")]
    [InlineData("Backroom.Cell.Host", "Backroom.ServiceDefaults")]
    [InlineData("Backroom.ControlPlane.Host", "Backroom.ServiceDefaults")]
    public void FindViolations_WhenReferenceIsAllowed_ReturnsNothing(string assembly, string reference)
    {
        ModuleDependencyRules.FindViolations(assembly, [reference]).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Backroom.Modules.Messaging.Application", "Backroom.Modules.Files.Domain")]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "Backroom.Modules.Files.Infrastructure")]
    [InlineData("Backroom.Modules.Messaging.Domain", "Backroom.Modules.Messaging.Application")]
    [InlineData("Backroom.Modules.Messaging.Domain", "Backroom.Modules.Messaging.Contracts")]
    [InlineData("Backroom.Modules.Messaging.Domain", "Backroom.BuildingBlocks.Infrastructure")]
    [InlineData("Backroom.Modules.Messaging.Domain", "Backroom.Modules.Files.Contracts")]
    [InlineData("Backroom.Modules.Messaging.Contracts", "Backroom.Modules.Files.Contracts")]
    [InlineData("Backroom.Modules.Messaging.Contracts", "Backroom.BuildingBlocks.Application")]
    [InlineData("Backroom.Modules.Messaging.Presentation", "Backroom.Modules.Messaging.Infrastructure")]
    [InlineData("Backroom.Modules.Messaging.Application", "Backroom.Modules.Files.Persistence")]
    [InlineData("Backroom.Modules.Messaging.Application", "Backroom.Cell.Host")]
    [InlineData("Backroom.BuildingBlocks.Infrastructure", "Backroom.ControlPlane.Host")]
    [InlineData("Backroom.BuildingBlocks.Infrastructure", "Backroom.Modules.Messaging.Domain")]
    [InlineData("Backroom.BuildingBlocks.Domain", "Backroom.Modules.Messaging.Contracts")]
    [InlineData("Backroom.BuildingBlocks.Domain", "Backroom.BuildingBlocks.Presentation")]
    [InlineData("Backroom.BuildingBlocks.Application", "Backroom.BuildingBlocks.Infrastructure")]
    [InlineData("Backroom.BuildingBlocks.Presentation", "Backroom.BuildingBlocks.Infrastructure")]
    [InlineData("Backroom.Modules.Messaging.Application", "Backroom.BuildingBlocks.Presentation")]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "Backroom.BuildingBlocks.Presentation")]
    [InlineData("Backroom.Modules.Messaging.Presentation", "Backroom.ServiceDefaults")]
    [InlineData("Backroom.BuildingBlocks.Infrastructure", "Backroom.ServiceDefaults")]
    [InlineData("Backroom.Cell.Host", "Backroom.AppHost")]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "Backroom.AppHost")]
    public void FindViolations_WhenReferenceIsForbidden_ReturnsViolation(string assembly, string reference)
    {
        ModuleDependencyRules.FindViolations(assembly, [reference]).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Backroom.Modules.Messaging")]
    [InlineData("Backroom.Modules.Messaging.Persistence")]
    [InlineData("Backroom.Modules.Messaging.Domain.Extra")]
    public void FindViolations_WhenModuleNameIsInvalid_ReturnsViolation(string assembly)
    {
        ModuleDependencyRules.FindViolations(assembly, []).Should().ContainSingle();
    }
}
