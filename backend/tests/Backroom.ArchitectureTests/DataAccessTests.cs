using AwesomeAssertions;

namespace Backroom.ArchitectureTests;

public sealed class DataAccessTests
{
    [Fact]
    public void CompiledAssemblies_OutsideInfrastructure_DoNotReferenceDataAccess()
    {
        var violations = SolutionAssemblies.All
            .SelectMany(assembly => DataAccessRules.FindViolations(
                assembly.GetName().Name!,
                assembly.GetReferencedAssemblies().Select(reference => reference.Name!)))
            .ToList();

        violations.Should().BeEmpty();
    }

    [Theory]
    [InlineData("Backroom.Modules.Messaging.Application", "Microsoft.EntityFrameworkCore")]
    [InlineData("Backroom.Modules.Messaging.Domain", "Npgsql")]
    [InlineData("Backroom.BuildingBlocks.Presentation", "Microsoft.EntityFrameworkCore.Relational")]
    [InlineData("Backroom.Modules.Messaging.Contracts", "Npgsql.EntityFrameworkCore.PostgreSQL")]
    public void FindViolations_WhenLayerUsesDataAccess_ReturnsViolation(string assembly, string reference)
    {
        DataAccessRules.FindViolations(assembly, [reference]).Should().ContainSingle();
    }

    [Theory]
    [InlineData("Backroom.Modules.Messaging.Infrastructure", "Microsoft.EntityFrameworkCore")]
    [InlineData("Backroom.BuildingBlocks.Infrastructure", "Npgsql")]
    [InlineData("Backroom.Modules.Messaging.Application", "Microsoft.Extensions.Logging")]
    public void FindViolations_WhenAllowed_ReturnsNothing(string assembly, string reference)
    {
        DataAccessRules.FindViolations(assembly, [reference]).Should().BeEmpty();
    }
}
