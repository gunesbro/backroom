namespace Backroom.Modules.Organization.Application.Members;

/// <summary>Members in the current unit of work, which row-level security limits to the entered organization.</summary>
internal interface IMemberStore
{
    /// <summary><c>active</c>, <c>deactivated</c> or <c>removed</c> if the account was ever a member; otherwise <see langword="null"/>.</summary>
    Task<string?> GetStatusAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds the account as an active member (version 1), or makes a removed member active again with the new profile
    /// (version + 1); the membership's new version. <see langword="null"/> if the account is an active or deactivated
    /// member, which nothing changes.
    /// </summary>
    Task<long?> JoinAsync(JoiningMember member, CancellationToken cancellationToken);
}
