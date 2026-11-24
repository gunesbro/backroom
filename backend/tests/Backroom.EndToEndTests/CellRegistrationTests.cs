using AwesomeAssertions;

namespace Backroom.EndToEndTests;

/// <summary>Both cells register with the control plane over the link and are offered to clients.</summary>
[Trait(TestCategory.Name, TestCategory.Short)]
public sealed class CellRegistrationTests(EndToEndEnvironment environment)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SharedCells_AfterStart_ListsBothCellsAsync()
    {
        Gate.RequireEnvironment();

        var cellIds = await environment.SharedCellIdsAsync(CancellationToken);

        cellIds.Should().Equal(EndToEndEnvironment.CellIds);
    }
}
