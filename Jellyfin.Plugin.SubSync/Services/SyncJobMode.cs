namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// The multi-subtitle sync modes and their capabilities.
///
/// <list type="bullet">
/// <item><c>normal</c> — one task at a time, audio analysed on every run.</item>
/// <item><c>parallel</c> — several media files at once, audio analysed per run.</item>
/// <item><c>fast</c> — one at a time, but a file's speech analysis is reused.</item>
/// <item><c>ultimate</c> — both: several media files at once, each file's speech analysis
/// computed once and reused by its remaining subtitles.</item>
/// </list>
/// </summary>
public static class SyncJobMode
{
    /// <summary>One task at a time, no reuse.</summary>
    public const string Normal = "normal";

    /// <summary>Several media files at once.</summary>
    public const string Parallel = "parallel";

    /// <summary>Reuse a file's speech analysis.</summary>
    public const string Fast = "fast";

    /// <summary>Parallel plus speech reuse.</summary>
    public const string Ultimate = "ultimate";

    /// <summary>Every accepted mode value.</summary>
    public static readonly string[] Allowed = { Normal, Parallel, Fast, Ultimate };

    /// <summary>Clamps any value to a supported mode.</summary>
    /// <param name="mode">Candidate mode.</param>
    /// <returns>A valid mode name.</returns>
    public static string Normalize(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
        {
            return Normal;
        }

        var value = mode.Trim().ToLowerInvariant();
        return Array.IndexOf(Allowed, value) >= 0 ? value : Normal;
    }

    /// <summary>True when the mode runs more than one task at a time.</summary>
    /// <param name="mode">Mode name.</param>
    /// <returns>True for parallel and ultimate.</returns>
    public static bool IsParallel(string mode) =>
        Normalize(mode) is Parallel or Ultimate;

    /// <summary>True when the mode reuses a media file's speech analysis.</summary>
    /// <param name="mode">Mode name.</param>
    /// <returns>True for fast and ultimate.</returns>
    public static bool UsesSpeechCache(string mode) =>
        Normalize(mode) is Fast or Ultimate;

    /// <summary>Short human description, for logs and status lines.</summary>
    /// <param name="mode">Mode name.</param>
    /// <returns>Description of what the mode does.</returns>
    public static string Describe(string? mode) => Normalize(mode) switch
    {
        Parallel => "parallel (several media files at once)",
        Fast => "fast (reusing each file's audio analysis)",
        Ultimate => "ultimate (parallel + reusing each file's audio analysis)",
        _ => "normal (one at a time)"
    };
}
