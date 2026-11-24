using Backroom.BuildingBlocks.Infrastructure.Fencing;

namespace Backroom.BuildingBlocks.IntegrationTests.Fencing;

/// <summary>A write fence the test opens and closes (the cell's is its confirmed epoch).</summary>
internal sealed class SwitchableFence : ICellWriteFence
{
    private volatile bool _open = true;

    public bool Open
    {
        get => _open;
        set => _open = value;
    }

    public ValueTask<bool> AllowsWritesAsync(CancellationToken cancellationToken) => ValueTask.FromResult(_open);
}
