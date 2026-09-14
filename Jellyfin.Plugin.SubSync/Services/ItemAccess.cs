using System.Security.Claims;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Decides who may act on which item.
/// </summary>
/// <remarks>
/// Deliberately free of Jellyfin's own types: every value goes in as a plain string or bool, so the
/// decision can be driven directly by the suite. A check that needs a live server is a check that will
/// not run, and this one guards writes.
///
/// Everything here fails closed. An account with no resolved identity, no enabled folders, or an item
/// with no resolved ancestors is denied - the same shape as the walk ceiling's "not known to be fast is
/// not fast". The one exception is an account with all-folders permission, which is Jellyfin saying
/// "this one sees everything".
/// </remarks>
internal static class ItemAccess
{
    /// <summary>
    /// Claim types that may carry the caller's account id, most specific first. Jellyfin's own
    /// authentication puts the id in the nameidentifier claim; the JWT forms are here because the same
    /// principal reaches a plugin through more than one door.
    /// </summary>
    internal static readonly string[] UserIdClaimTypes =
    {
        ClaimTypes.NameIdentifier,
        "nameid",
        "sub",
        "Jellyfin-UserId",
    };

    /// <summary>
    /// Gets the account id from a request's claims, or null when there is none to trust.
    /// </summary>
    /// <param name="claims">Claim type and value pairs, in any order.</param>
    /// <returns>The account id, or null.</returns>
    internal static Guid? UserIdFrom(IEnumerable<KeyValuePair<string, string>> claims)
    {
        if (claims is null)
        {
            return null;
        }

        var byType = claims.ToLookup(c => c.Key, c => c.Value);
        foreach (var type in UserIdClaimTypes)
        {
            foreach (var value in byType[type])
            {
                if (Guid.TryParse(value, out var id) && id != Guid.Empty)
                {
                    return id;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Whether an account may act on an item, from the account's folder permission and the folders the
    /// item lives in.
    /// </summary>
    /// <param name="allFolders">Whether the account has all-folders permission.</param>
    /// <param name="allowedFolderIds">Ids of the libraries the account may see.</param>
    /// <param name="itemAncestorIds">Ids of every folder the item sits under, itself excluded.</param>
    /// <returns>True when the account may act on the item.</returns>
    internal static bool Allows(
        bool allFolders,
        IReadOnlyCollection<string>? allowedFolderIds,
        IReadOnlyCollection<string>? itemAncestorIds)
    {
        if (allFolders)
        {
            return true;
        }

        if (allowedFolderIds is null || allowedFolderIds.Count == 0)
        {
            return false;
        }

        if (itemAncestorIds is null || itemAncestorIds.Count == 0)
        {
            // An item with no ancestors cannot be placed in anyone's library, so it is not allowed to
            // anyone. The alternative - allowing it - is the bug this guards against.
            return false;
        }

        foreach (var ancestor in itemAncestorIds)
        {
            foreach (var allowed in allowedFolderIds)
            {
                if (string.Equals(Normalise(ancestor), Normalise(allowed), StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the first candidate an account may not act on, for the requests that carry a whole list.
    /// </summary>
    /// <typeparam name="T">The candidate type, e.g. a task or an item id.</typeparam>
    /// <param name="allFolders">Whether the account has all-folders permission.</param>
    /// <param name="allowedFolderIds">Ids of the libraries the account may see.</param>
    /// <param name="candidates">What the request asked for.</param>
    /// <param name="ancestorsOf">How to get a candidate's folder ids.</param>
    /// <returns>The first denied candidate, or null when the whole list is allowed.</returns>
    internal static T? FirstDenied<T>(
        bool allFolders,
        IReadOnlyCollection<string>? allowedFolderIds,
        IEnumerable<T> candidates,
        Func<T, IReadOnlyCollection<string>> ancestorsOf)
        where T : class
    {
        if (allFolders)
        {
            return null;
        }

        foreach (var candidate in candidates)
        {
            if (!Allows(false, allowedFolderIds, ancestorsOf(candidate)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Normalises an id for comparison: Jellyfin hands the same folder out as both the dashed and the
    /// undashed form depending on where it came from, and a mismatch here denies a user their own library.
    /// </summary>
    /// <param name="id">A folder id in either form.</param>
    /// <returns>The id without dashes.</returns>
    private static string Normalise(string id) => id.Replace("-", string.Empty, StringComparison.Ordinal);
}
