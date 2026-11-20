namespace Backroom.BuildingBlocks.IntegrationTests.Persistence;

public sealed class Note
{
    public Guid Id { get; set; }

    public Guid OrganizationId { get; set; }

    public string Text { get; set; } = string.Empty;
}
