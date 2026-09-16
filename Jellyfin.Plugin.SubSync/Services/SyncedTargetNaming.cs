using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// The files this plugin writes beside a video: their names, and the guards that keep them inside the tree.
/// </summary>
/// <remarks>
/// Split out of <c>SubSyncService</c> (the C7 cluster of <c>knowledge/SUBSYNCSERVICE_MAP.md</c>). The naming
/// rule is a rule about Jellyfin rather than about this plugin - a sidecar is only associated with an episode
/// when its first field is the media filename exactly - and the two guards are what keep a recursive delete
/// inside the plugin's own scratch root (B23) and a write out of a folder the Jellyfin user cannot write (S44).
/// No member here touches service state.
/// </remarks>
public static class SyncedTargetNaming
{
    /// <summary>
    /// Checks whether a folder can be written to, so a library the Jellyfin user cannot write to
    /// fails once with a clear reason instead of reporting an access error for every subtitle in
    /// it.
    ///
    /// This happens on read-only mounts, on shares that map a different owner, and on folders the
    /// container user cannot write. Detecting it in advance turns "Access to the path is denied"
    /// repeated a hundred times into one sentence naming the folder.
    /// </summary>
    /// <param name="directory">Folder the synced subtitle would be written to.</param>
    /// <param name="reason">Why it cannot be written, when it cannot.</param>
    /// <returns>True when a file could be created there.</returns>
    public static bool CanWriteTo(string directory, out string reason)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".subsync-write-probe-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            reason = string.Empty;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "the Jellyfin user has no write permission there (the folder may also be mounted read-only)";
            return false;
        }
        catch (IOException ex)
        {
            // The exception message is the useful part here: "Read-only file system" and
            // "Permission denied" mean different fixes.
            reason = "the folder could not be written to: " + ex.Message;
            return false;
        }
        catch (NotSupportedException)
        {
            reason = "the path is not a writable folder";
            return false;
        }
    }

    /// <summary>
    /// Throws a message that explains a folder the plugin cannot write to, and states plainly that
    /// nothing was changed.
    /// </summary>
    /// <param name="directory">Folder to check.</param>
    internal static void RequireWritable(string directory)
    {
        if (!CanWriteTo(directory, out var reason))
        {
            throw new InvalidOperationException(
                $"Cannot write the synced subtitle to '{directory}': {reason}. "
                + "Nothing was changed and the original subtitle is untouched. "
                + "Fix the folder's permissions for the Jellyfin user (or how the library is mounted) and run again.");
        }
    }

    /// <summary>
    /// Names the sidecar the plugin writes for a subtitle (S12).
    /// </summary>
    /// <remarks>
    /// Jellyfin associates a sidecar with its video only when the name starts with the media file's name and continues
    /// with dot-separated fields, so the marker is a field and never part of the name. A stem the plugin already marked
    /// loses that marker first: re-syncing the plugin's own output updates that file instead of writing
    /// <c>Film.SYNCED.ukr.SYNCED.srt</c>, which no player shows and no sweep removes.
    /// </remarks>
    /// <param name="directory">The directory to write into.</param>
    /// <param name="stem">The input file's name without its extension.</param>
    /// <param name="language">The track's language, when it has one.</param>
    /// <returns>The full path to write.</returns>
    internal static string SyncedTargetName(string directory, string stem, string? language)
    {
        var lang = string.IsNullOrWhiteSpace(language) ? null : language.Trim().ToLowerInvariant();
        var baseStem = SrtWriter.StripSyncedMarker(stem);
        return lang is not null && string.Equals(baseStem, lang, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(directory, $"{lang}.SYNCED.srt")
            : Path.Combine(directory, baseStem + ".SYNCED.srt");
    }

    /// <summary>
    /// Asks whether a directory name is one this plugin creates for a job's scratch space (B23).
    /// </summary>
    /// <remarks>
    /// Job ids are <c>Guid.NewGuid().ToString("N")</c> - 32 lowercase hex characters and nothing else - so the
    /// test is exact rather than a prefix or a "looks like an id" match. That is what keeps a recursive delete
    /// away from every other directory that can live beside them.
    /// </remarks>
    /// <param name="name">Directory name.</param>
    /// <returns>True when the name is a job id.</returns>
    internal static bool IsJobScratchDirectory(string? name)
        => !string.IsNullOrEmpty(name)
           && name.Length == 32
           && name.All(character => (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));

    /// <summary>
    /// Asks whether a path is inside a root directory, resolved (B23).
    /// </summary>
    /// <param name="rootFull">Fully resolved root.</param>
    /// <param name="path">Path to test.</param>
    /// <returns>True when the path is the root itself or below it.</returns>
    internal static bool IsInsideRoot(string rootFull, string path)
    {
        try
        {
            var candidate = Path.GetFullPath(path);
            var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
