namespace Backroom.Modules.Directory.Application.Invitations;

/// <summary>
/// One batch of an invitation job: how many rows it <paramref name="Selected"/> (at most the limit; fewer means
/// nothing more is due) and how many it <paramref name="Changed"/> (fewer if some were busy and skipped), with the
/// ids of the invitations it <paramref name="Expired"/> (empty for retention).
/// </summary>
internal sealed record InvitationJobBatch(int Selected, int Changed, IReadOnlyList<ExpiredInvitation> Expired)
{
    public static InvitationJobBatch Counted(int selected, int changed) => new(selected, changed, []);
}
