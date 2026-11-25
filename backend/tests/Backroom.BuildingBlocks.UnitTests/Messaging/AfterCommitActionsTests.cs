using AwesomeAssertions;
using Backroom.BuildingBlocks.Application.Messaging;

namespace Backroom.BuildingBlocks.UnitTests.Messaging;

public sealed class AfterCommitActionsTests
{
    [Fact]
    public async Task Run_ExecutesEachActionOnceAndClearsThemAsync()
    {
        var actions = new AfterCommitActions();
        var calls = 0;
        actions.Add(_ =>
        {
            calls++;
            return Task.CompletedTask;
        });

        await actions.RunAsync(CancellationToken.None);
        await actions.RunAsync(CancellationToken.None);

        calls.Should().Be(1);
    }

    [Fact]
    public async Task Run_WhenAnActionThrows_SkipsItAndRunsTheRestAsync()
    {
        var actions = new AfterCommitActions();
        var ran = false;
        actions.Add(_ => throw new InvalidOperationException("boom"));
        actions.Add(_ =>
        {
            ran = true;
            return Task.CompletedTask;
        });

        await actions.RunAsync(CancellationToken.None);

        ran.Should().BeTrue();
    }

    [Fact]
    public async Task Run_WhenNothingRanBeforeCommit_DoesNothingAsync()
    {
        // A failed commit never reaches RunAsync; an unused instance is harmless.
        await new AfterCommitActions().RunAsync(CancellationToken.None);
    }
}
