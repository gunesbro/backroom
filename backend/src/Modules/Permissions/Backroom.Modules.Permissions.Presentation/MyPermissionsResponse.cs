namespace Backroom.Modules.Permissions.Presentation;

/// <summary>The caller's own permission keys in the organization, in ordinal order.</summary>
public sealed record MyPermissionsResponse(IReadOnlyList<string> Permissions);
