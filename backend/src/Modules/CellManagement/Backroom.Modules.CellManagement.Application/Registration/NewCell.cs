using System.Text.RegularExpressions;

namespace Backroom.Modules.CellManagement.Application.Registration;

/// <summary>A cell to be registered: its id becomes the token audience <c>cell-{id}</c>.</summary>
internal sealed partial record NewCell(string Id, string Name, string Type, string Region, Uri PublicUrl)
{
    public static readonly string[] Types = ["shared", "dedicated", "hybrid"];

    public bool IsValid() =>
        Id is not null && Name is not null && Type is not null && Region is not null
        && CellIdPattern().IsMatch(Id)
        && !string.IsNullOrWhiteSpace(Name) && Name.Length <= 100
        && Types.Contains(Type, StringComparer.Ordinal)
        && (Type != "shared" || Id.Length <= SharedCellOptions.MaxIdLength)
        && RegionPattern().IsMatch(Region)
        && PublicUrl is { IsAbsoluteUri: true } && (PublicUrl.Scheme == Uri.UriSchemeHttps || PublicUrl.IsLoopback);

    // The same rule as the audience (Audiences.Cell in BuildingBlocks.Presentation).
    [GeneratedRegex(@"\A[a-z0-9][a-z0-9-]{0,62}\z", RegexOptions.CultureInvariant)]
    private static partial Regex CellIdPattern();

    [GeneratedRegex(@"\A[a-z]{2}(-[a-z0-9]+){0,3}\z", RegexOptions.CultureInvariant)]
    private static partial Regex RegionPattern();
}
