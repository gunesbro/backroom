using AwesomeAssertions;

namespace Backroom.ArchitectureTests;

public sealed class ModuleDependencyTests
{
    [Fact]
    public void ProjectReferences_WhenDeclared_FollowTheRules()
    {
        ProjectReferenceGraph.Projects.Should().NotBeEmpty();

        var violations = ProjectReferenceGraph.Projects
            .SelectMany(project => ModuleDependencyRules.FindViolations(project.Key, project.Value))
            .ToList();

        violations.Should().BeEmpty();
    }

    [Fact]
    public void CompiledAssemblies_WhenLoaded_ReferenceOnlyAllowedAssemblies()
    {
        var violations = SolutionAssemblies.All
            .SelectMany(assembly => ModuleDependencyRules.FindViolations(
                assembly.GetName().Name!,
                assembly.GetReferencedAssemblies().Select(reference => reference.Name!)))
            .ToList();

        violations.Should().BeEmpty();
    }
}
