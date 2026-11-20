namespace Backroom.Modules.Organization.Contracts;

/// <summary>An organization the control plane assigned to this cell, and its owner (from the owner's token).</summary>
public sealed record NewOrganization(
    Guid Id,
    string Name,
    Guid OwnerAccountId,
    string OwnerDisplayName,
    string OwnerEmail,
    string OwnerLocale);
