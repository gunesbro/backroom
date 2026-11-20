namespace Backroom.BuildingBlocks.Application.Organizations;

/// <summary>
/// The organization the current request or message handler works in. The unit of work applies it to
/// the database transaction, and row-level security limits every query to it (docs/adr/0005).
/// </summary>
public interface IOrganizationContext
{
    /// <summary>The organization, or <see langword="null"/> outside an organization; then no organization rows are visible.</summary>
    Guid? OrganizationId { get; }
}
