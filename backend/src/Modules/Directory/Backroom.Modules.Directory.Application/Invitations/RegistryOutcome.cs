namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>What storing an upstream invitation message did.</summary>
internal enum RegistryOutcome
{
    /// <summary>The message changed the invitation (or created it, or left a revocation tombstone).</summary>
    Applied,

    /// <summary>A revocation reached an invitation being accepted: only the request was recorded.</summary>
    RevokeRequested,

    /// <summary>Nothing to change: an older or repeated code version, or a terminal or revoked invitation.</summary>
    Ignored,

    /// <summary>The invitation id belongs to another organization: the cell cannot speak for it.</summary>
    OtherOrganization,

    /// <summary>Another invitation already has this code hash.</summary>
    CodeTaken,
}
