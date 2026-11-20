using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Backroom.ArchitectureTests;

/// <summary>
/// Type-level rules for the shared building blocks. Project-level layering is covered by
/// <see cref="ModuleDependencyTests"/>; these rules catch framework dependencies that arrive through NuGet packages.
/// </summary>
public sealed class BuildingBlocksLayerTests
{
    private const string AspNetCoreNamespaces = @"^Microsoft\.AspNetCore(\..*)?$";
    private const string EntityFrameworkNamespaces = @"^Microsoft\.EntityFrameworkCore(\..*)?$";

    private static readonly System.Reflection.Assembly s_domain = SolutionAssemblies.Get("Backroom.BuildingBlocks.Domain");
    private static readonly System.Reflection.Assembly s_application = SolutionAssemblies.Get("Backroom.BuildingBlocks.Application");

    private static readonly Architecture s_architecture = new ArchLoader()
        .LoadAssemblies(s_domain, s_application)
        .Build();

    [Fact]
    public void Domain_WhenChecked_DoesNotDependOnWebOrPersistenceFrameworks()
    {
        Types().That().ResideInAssembly(s_domain)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(AspNetCoreNamespaces)
            .AndShould().NotDependOnAnyTypesThat().ResideInNamespaceMatching(EntityFrameworkNamespaces)
            .WithoutRequiringPositiveResults()
            .Check(s_architecture);
    }

    [Fact]
    public void Application_WhenChecked_DoesNotDependOnPersistenceFramework()
    {
        Types().That().ResideInAssembly(s_application)
            .Should().NotDependOnAnyTypesThat().ResideInNamespaceMatching(EntityFrameworkNamespaces)
            .WithoutRequiringPositiveResults()
            .Check(s_architecture);
    }
}
