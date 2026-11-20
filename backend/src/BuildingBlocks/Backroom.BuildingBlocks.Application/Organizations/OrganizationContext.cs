namespace Backroom.BuildingBlocks.Application.Organizations;

/// <summary>
/// Scoped holder of the organization context. Only trusted pipeline code enters an organization:
/// the membership check for requests (the route's organization, for the authenticated account) and
/// the message pipeline (from the message's <c>org_id</c>). The check itself reads under row-level
/// security, so it enters first; no handler runs for a client until the check has passed.
/// </summary>
public sealed class OrganizationContext : IOrganizationContext
{
    public Guid? OrganizationId { get; private set; }

    /// <summary>
    /// Enters <paramref name="organizationId"/>. A scope works in at most one organization, so entering
    /// a different one throws instead of silently switching.
    /// </summary>
    public void Enter(Guid organizationId)
    {
        if (organizationId == Guid.Empty)
        {
            throw new ArgumentException("The organization id must not be empty.", nameof(organizationId));
        }

        if (OrganizationId is { } current && current != organizationId)
        {
            throw new InvalidOperationException("This scope already works in another organization.");
        }

        OrganizationId = organizationId;
    }
}
