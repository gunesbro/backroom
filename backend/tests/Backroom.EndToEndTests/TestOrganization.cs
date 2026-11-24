namespace Backroom.EndToEndTests;

/// <summary>An organization created through the control plane (flow 2), with the cell that holds it.</summary>
internal sealed record TestOrganization(Guid Id, string CellId, Uri CellUrl)
{
    /// <summary>The organization's API on its cell, <c>{cell}/api/v1/orgs/{id}/{path}</c>.</summary>
    public Uri Api(string path) => new(CellUrl, $"/api/v1/orgs/{Id}/{path}");
}
