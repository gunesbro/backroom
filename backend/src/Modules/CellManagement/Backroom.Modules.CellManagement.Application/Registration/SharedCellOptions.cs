namespace Backroom.Modules.CellManagement.Application.Registration;

/// <summary>
/// Shared cells (docs/contracts/phase-1.md, "Audience per target"): sign-in requests every shared cell's scope, so
/// their number and id length keep the authorization URL and the refresh token small.
/// </summary>
internal sealed class SharedCellOptions
{
    public const string SectionName = "CellManagement:SharedCells";

    /// <summary>The contract's limit until pushed authorization requests (RFC 9126) arrive.</summary>
    public const int ContractMax = 20;

    /// <summary>Longer ids would make every sign-in's scope list longer.</summary>
    public const int MaxIdLength = 16;

    /// <summary>How many shared cells may exist and are listed; at most <see cref="ContractMax"/> outside Development.</summary>
    public int Max { get; set; } = ContractMax;
}
