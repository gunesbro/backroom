using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Backroom.Modules.CellManagement.Contracts;
using Backroom.Testing.Keycloak;
using Backroom.Testing.Postgres;

namespace Backroom.Modules.Directory.IntegrationTests;

/// <summary>The operations panel's organization counts per cell: failed organizations are not counted, the lookup is bounded.</summary>
[Collection(DirectoryCollection.Name)]
public sealed class CellOrganizationCountsTests(PostgresDatabase database, KeycloakRealm keycloak) : IAsyncLifetime
{
    private WebApplication _controlPlane = null!;

    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() => _controlPlane = await DirectoryUnderTest.StartAsync(database, keycloak, CancellationToken);

    public async ValueTask DisposeAsync() => await _controlPlane.DisposeAsync();

    [Fact]
    public async Task CountByCell_WithOrganizationsOnTwoCells_CountsEachCellAsync()
    {
        var (first, second, empty) = (NewCellId(), NewCellId(), NewCellId());
        await InsertOrganizationAsync(first, "active");
        await InsertOrganizationAsync(first, "provisioning");
        await InsertOrganizationAsync(second, "active");

        var counts = await Counts().CountByCellAsync([first, second, empty], CancellationToken);

        counts[first].Should().Be(2);
        counts[second].Should().Be(1);
        counts.Should().NotContainKey(empty, "a cell without organizations is left out");
    }

    [Fact]
    public async Task CountByCell_WithFailedOrganizations_LeavesThemOutAsync()
    {
        var cell = NewCellId();
        await InsertOrganizationAsync(cell, "active");
        await InsertOrganizationAsync(cell, "failed");

        var counts = await Counts().CountByCellAsync([cell], CancellationToken);

        counts[cell].Should().Be(1);
    }

    [Fact]
    public async Task CountByCell_WithOtherCellsOrganizations_IgnoresThemAsync()
    {
        var (asked, other) = (NewCellId(), NewCellId());
        await InsertOrganizationAsync(other, "active");

        var counts = await Counts().CountByCellAsync([asked], CancellationToken);

        counts.Should().BeEmpty();
    }

    [Fact]
    public async Task CountByCell_WithMoreCellsThanTheLimit_IsRefusedAsync()
    {
        var ids = Enumerable.Range(0, ICellOrganizationCounts.MaxCellsPerLookup + 1).Select(i => "c" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)).ToList();

        var count = () => Counts().CountByCellAsync(ids, CancellationToken);

        await count.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public async Task CountByCell_WithNoCells_ReturnsNothingAsync()
    {
        var counts = await Counts().CountByCellAsync([], CancellationToken);

        counts.Should().BeEmpty();
    }

    private ICellOrganizationCounts Counts() => _controlPlane.Services.GetRequiredService<ICellOrganizationCounts>();

    private static string NewCellId() => "t" + Guid.CreateVersion7().ToString("N")[^10..];

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA2100", Justification = "Test seeding with constant SQL; values are parameters.")]
    private async Task InsertOrganizationAsync(string cellId, string status)
    {
        await using var connection = new NpgsqlConnection(database.SuperuserConnectionString);
        await connection.OpenAsync(CancellationToken);
        await using var command = new NpgsqlCommand(
            "INSERT INTO directory.organizations (id, name, region, cell_id, status, created_by, created_at) VALUES ($1, 'Org', 'eu-test', $2, $3, $4, now())",
            connection);
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        command.Parameters.Add(new NpgsqlParameter { Value = cellId });
        command.Parameters.Add(new NpgsqlParameter { Value = status });
        command.Parameters.Add(new NpgsqlParameter { Value = Guid.CreateVersion7() });
        await command.ExecuteNonQueryAsync(CancellationToken);
    }
}
