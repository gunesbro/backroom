using System.Net;
using AwesomeAssertions;

namespace Backroom.EndToEndTests;

/// <summary>Flow 2: the control plane places organizations on the cells, and each cell provisions its own.</summary>
[Trait(TestCategory.Name, TestCategory.Short)]
public sealed class OrganizationTests(EndToEndEnvironment environment)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task CreateOrganization_TwiceInAFreshRun_ProvisionsOneOnEachCellAsync()
    {
        Gate.RequireEnvironment();

        var organizations = await environment.OrganizationsAsync(CancellationToken);

        organizations.Select(organization => organization.CellId).Should().BeEquivalentTo(EndToEndEnvironment.CellIds);
        foreach (var organization in organizations)
        {
            // The address clients get is the cell's own endpoint in this run, not a fixed development port.
            organization.CellUrl.Should().Be(environment.CellUrl(organization.CellId));
        }
    }

    [Fact]
    public async Task MyOrganizations_AfterCreation_ListsThemWithTheirCellsAsync()
    {
        Gate.RequireEnvironment();
        var organizations = await environment.OrganizationsAsync(CancellationToken);
        var cellIds = await environment.SharedCellIdsAsync(CancellationToken);
        using var alice = await SignedInPerson.SignInAsync(environment, EndToEndEnvironment.Alice, cellIds, CancellationToken);
        var token = await alice.TokenForAsync("control-plane", CancellationToken);

        var mine = await Api.GetAsync(environment.Http, environment.ControlPlane("/api/v1/me/organizations"), token.Value, CancellationToken);

        mine.Status.Should().Be(HttpStatusCode.OK);
        var listed = mine.Json["organizations"]!.AsArray()
            .Select(organization => (Id: organization!["id"]!.GetValue<Guid>(), CellId: organization["cellId"]!.GetValue<string>()));
        listed.Should().Contain(organizations.Select(organization => (organization.Id, organization.CellId)));
    }
}
