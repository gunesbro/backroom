namespace Backroom.Modules.CellManagement.Infrastructure;

internal sealed class DevelopmentCell
{
    public string Id { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public string Region { get; set; } = "eu-dev";

    public Uri? PublicUrl { get; set; }

    /// <summary>A secret from the AppHost's user secrets.</summary>
    public string RegistrationKey { get; set; } = string.Empty;
}
