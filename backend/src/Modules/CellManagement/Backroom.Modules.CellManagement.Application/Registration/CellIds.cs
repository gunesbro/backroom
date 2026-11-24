using System.Text.RegularExpressions;

namespace Backroom.Modules.CellManagement.Application.Registration;

/// <summary>The cell id rule, the same as the audience (Audiences.Cell in BuildingBlocks.Presentation).</summary>
internal static partial class CellIds
{
    public static bool IsValid(string? id) => id is not null && Pattern().IsMatch(id);

    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();
}
