using System.Text.RegularExpressions;

namespace Backroom.BuildingBlocks.Presentation.Authentication;

/// <summary>Token audiences per target (docs/contracts/phase-1.md, "Identity and tokens").</summary>
public static partial class Audiences
{
    public const string ControlPlane = "control-plane";

    /// <summary>The audience of cell <paramref name="cellId"/>: <c>cell-{cellId}</c>.</summary>
    public static string Cell(string cellId)
    {
        ArgumentNullException.ThrowIfNull(cellId);
        if (!CellIdPattern().IsMatch(cellId))
        {
            throw new ArgumentException($"Cell id '{cellId}' must be lower-case letters, digits and hyphens.", nameof(cellId));
        }

        return "cell-" + cellId;
    }

    internal static bool IsValidCellId(string cellId) => CellIdPattern().IsMatch(cellId);

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CellIdPattern();
}
