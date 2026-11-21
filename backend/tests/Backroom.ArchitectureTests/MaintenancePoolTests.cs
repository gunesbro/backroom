using System.Text.RegularExpressions;
using ArchUnitNET.Domain;
using ArchUnitNET.Loader;
using ArchUnitNET.xUnitV3;
using AwesomeAssertions;
using static ArchUnitNET.Fluent.ArchRuleDefinition;

namespace Backroom.ArchitectureTests;

/// <summary>
/// The maintenance role bypasses row-level security, so its connection pool must stay inside the job runner
/// (docs/adr/0010): no type outside the <c>Maintenance</c> namespace uses <c>MaintenanceDataSource</c>, and
/// no data source for the maintenance role is registered in dependency injection, where a request handler
/// could obtain it. The relay's keyed data sources are not covered.
/// </summary>
public sealed partial class MaintenancePoolTests
{
    private const string MaintenanceNamespace = "Backroom.BuildingBlocks.Infrastructure.Maintenance";

    private static readonly Architecture s_architecture = new ArchLoader().LoadAssemblies(SolutionAssemblies.All.ToArray()).Build();

    [Fact]
    public void Types_OutsideTheMaintenanceNamespace_DoNotUseTheMaintenanceDataSource()
    {
        Types().That().DoNotResideInNamespaceMatching(@"^Backroom\.BuildingBlocks\.Infrastructure\.Maintenance(\..*)?$")
            .Should().NotDependOnAnyTypesThat().HaveFullName(MaintenanceNamespace + ".MaintenanceDataSource")
            .WithoutRequiringPositiveResults()
            .Check(s_architecture);
    }

    [Fact]
    public void Types_InsideTheMaintenanceNamespace_ExposeNoNpgsqlDataSource()
    {
        // Constructor parameters and public members of other types would let dependency injection hand it out.
        Types().That().ResideInNamespaceMatching(@"^Backroom\.BuildingBlocks\.Infrastructure\.Maintenance(\..*)?$")
            .And().DoNotHaveFullName(MaintenanceNamespace + ".MaintenanceDataSource")
            .Should().NotDependOnAnyTypesThat().HaveFullName("Npgsql.NpgsqlDataSource")
            .WithoutRequiringPositiveResults()
            .Check(s_architecture);
    }

    // Building each host's service collection here is not feasible: the hosts need Keycloak, RabbitMQ and database
    // settings to start, and this project only loads assemblies. The static scan below covers the same ground at the
    // source level: no file outside the runner mentions the maintenance role or its settings (the
    // role name, MaintenanceJobOptions, MaintenanceJobs, MaintenanceConnectionString) together with a data source,
    // so no NpgsqlDataSource registration can use backroom_maintenance.
    [Fact]
    public void Sources_DoNotRegisterOrBuildADataSourceForTheMaintenanceRoleOutsideTheRunner()
    {
        var offenders = Directory.EnumerateFiles(RepositoryPaths.SourceDirectory, "*.cs", SearchOption.AllDirectories)
            .Where(path => !IsGenerated(path))
            .Where(path => FindViolation(path, File.ReadAllText(path)) is not null)
            .Select(path => Path.GetRelativePath(RepositoryPaths.SourceDirectory, path))
            .ToList();

        offenders.Should().BeEmpty();
    }

    [Theory]
    [InlineData("services.AddSingleton(NpgsqlDataSource.Create(options.MaintenanceConnectionString));")]
    [InlineData("services.AddNpgsqlDataSource(configuration[\"MaintenanceJobs:ConnectionString\"]!);")]
    [InlineData("services.AddKeyedSingleton(\"maintenance\", NpgsqlDataSource.Create(configuration.GetSection(MaintenanceJobOptions.SectionName)[\"ConnectionString\"]!));")]
    [InlineData("new NpgsqlDataSourceBuilder(maintenance.ConnectionString) // MaintenanceJobOptions")]
    public void FindViolation_WhenAMaintenanceSettingFeedsADataSource_ReturnsIt(string source)
    {
        FindViolation("Hosts/Some.Host/Program.cs", source).Should().NotBeNull();
    }

    [Theory]
    [InlineData("Hosts/Some.Host/Program.cs", "services.AddSingleton(NpgsqlDataSource.Create(options.ConnectionString));")]
    [InlineData("Hosts/Some.Host/Program.cs", "services.AddMaintenanceJobRunner().BindConfiguration(MaintenanceJobOptions.SectionName);")]
    [InlineData("BuildingBlocks/Backroom.BuildingBlocks.Infrastructure/Maintenance/MaintenanceDataSource.cs", "new NpgsqlDataSourceBuilder(options.ConnectionString) // MaintenanceJobOptions")]
    public void FindViolation_WhenAllowed_ReturnsNothing(string path, string source)
    {
        FindViolation(path.Replace('/', Path.DirectorySeparatorChar), source).Should().BeNull();
    }

    private static string? FindViolation(string path, string source)
    {
        var inMaintenanceFolder = path.Contains($"{Path.DirectorySeparatorChar}Maintenance{Path.DirectorySeparatorChar}", StringComparison.Ordinal);

        // Inside the Maintenance folder only MaintenanceDataSource may build a data source.
        if (inMaintenanceFolder)
        {
            return Path.GetFileName(path) is not "MaintenanceDataSource.cs" && DataSourceUse().IsMatch(source)
                ? "builds a data source inside the Maintenance folder"
                : null;
        }

        return MaintenanceSetting().IsMatch(source) && DataSourceUse().IsMatch(source)
            ? "turns a maintenance setting into a data source"
            : null;
    }

    private static bool IsGenerated(string path)
    {
        var separator = Path.DirectorySeparatorChar;
        return path.Contains($"{separator}obj{separator}", StringComparison.Ordinal) || path.Contains($"{separator}bin{separator}", StringComparison.Ordinal);
    }

    [GeneratedRegex(@"MaintenanceJobOptions|MaintenanceJobs|MaintenanceConnectionString|backroom_maintenance", RegexOptions.CultureInvariant)]
    private static partial Regex MaintenanceSetting();

    [GeneratedRegex(@"NpgsqlDataSource|AddNpgsqlDataSource|AddKeyedNpgsqlDataSource|new\s+NpgsqlConnection\s*\(", RegexOptions.CultureInvariant)]
    private static partial Regex DataSourceUse();
}
