using AwesomeAssertions;

namespace Backroom.EndToEndTests;

/// <summary>Flow 1: one sign-in, then a token per target, each for exactly one audience.</summary>
[Trait(TestCategory.Name, TestCategory.Short)]
public sealed class SignInTests(EndToEndEnvironment environment)
{
    private static CancellationToken CancellationToken => TestContext.Current.CancellationToken;

    [Fact]
    public async Task SignIn_NarrowedToEachTarget_GivesTokensWithOneAudienceAsync()
    {
        Gate.RequireEnvironment();
        var cellIds = await environment.SharedCellIdsAsync(CancellationToken);
        using var alice = await SignedInPerson.SignInAsync(environment, EndToEndEnvironment.Alice, cellIds, CancellationToken);

        var controlPlane = await alice.TokenForAsync("control-plane", CancellationToken);
        var cellA = await alice.TokenForAsync("cell:a", CancellationToken);
        var cellB = await alice.TokenForAsync("cell:b", CancellationToken);

        controlPlane.Audiences.Should().Equal("control-plane");
        cellA.Audiences.Should().Equal("cell-a");
        cellB.Audiences.Should().Equal("cell-b");
    }
}
