namespace Backroom.Modules.Messaging.Application.Conversations;

/// <summary>A list of account ids from a request body.</summary>
internal static class AccountIdList
{
    /// <summary>
    /// The distinct ids in a fixed order (so batches lock rows in the same order), without <paramref name="exclude"/>;
    /// <see langword="false"/> for more than <paramref name="max"/> entries or an empty id. No list is an empty one.
    /// </summary>
    public static bool TryDistinct(IReadOnlyList<Guid>? ids, int max, Guid? exclude, out IReadOnlyList<Guid> distinct)
    {
        distinct = [];
        if (ids is null)
        {
            return true;
        }

        if (ids.Count > max || ids.Contains(Guid.Empty))
        {
            return false;
        }

        distinct = [.. ids.Where(id => id != exclude).Distinct().Order()];
        return true;
    }
}
