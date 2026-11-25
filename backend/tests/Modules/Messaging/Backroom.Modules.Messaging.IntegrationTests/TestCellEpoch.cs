using Backroom.BuildingBlocks.Application.Cells;

namespace Backroom.Modules.Messaging.IntegrationTests;

/// <summary>The cell's epoch as a test sets it; <see langword="null"/> is a cell that has not confirmed one.</summary>
internal sealed class TestCellEpoch : ICellEpoch
{
    public const long Initial = 7;

    public long? Epoch { get; set; } = Initial;

    public ValueTask<long?> ConfirmedEpochAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Epoch);
}
