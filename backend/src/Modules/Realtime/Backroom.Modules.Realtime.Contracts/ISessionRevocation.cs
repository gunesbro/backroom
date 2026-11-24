namespace Backroom.Modules.Realtime.Contracts;

/// <summary>
/// Applies a session revocation in the cell (docs/contracts/phase-1.md, "Identity and tokens" and flow 5), called by the
/// control plane connector for <c>SessionRevoked</c> and <c>FullSync</c>. It stores the revocation until its <c>keep_until</c>,
/// so the cell's authentication refuses the session's tokens, and closes the matching hub connections on every app node.
/// Applying the same revocation twice is harmless. It throws when the revocation could not be stored or announced (the
/// connector then lets the control plane send it again); an already expired revocation is ignored.
/// </summary>
public interface ISessionRevocation
{
    Task RevokeAsync(SessionRevocation revocation, CancellationToken cancellationToken);
}
