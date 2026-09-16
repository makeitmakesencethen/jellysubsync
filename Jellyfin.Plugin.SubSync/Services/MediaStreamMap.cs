using System.Globalization;
using MediaBrowser.Controller.Entities;
using System.Runtime.InteropServices;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// What a media file's subtitle streams are, and which of them a job may use.
/// </summary>
/// <remarks>
/// Split out of <c>SubSyncService</c> (the C7 cluster of <c>knowledge/SUBSYNCSERVICE_MAP.md</c>) because
/// every member here answers a question about the file rather than about the queue: which container stream a
/// Jellyfin stream index corresponds to, which track an ordinal names, which track may serve as a ruler, and
/// whether an external subtitle is one this plugin wrote. Nothing here touches service state, which is what
/// makes the move a pure one.
/// </remarks>
public static class MediaStreamMap
{
    /// <summary>
    /// Locates the REAL container stream index of an embedded subtitle track by
    /// probing the file with ffmpeg. Jellyfin's MediaStream.Index cannot be used
    /// as a container index (values have been observed pointing past the file's
    /// actual stream count when a video mixes embedded and external subtitles),
    /// so the target is matched by position: the Nth embedded subtitle stream
    /// Jellyfin reports corresponds to the Nth subtitle stream ffmpeg sees.
    /// </summary>
    /// <param name="video">The video item.</param>
    /// <param name="target">The embedded subtitle stream to extract.</param>
    /// <returns>The container index, subtitle ordinal and codecs in subtitle-stream order.</returns>
    /// <param name="ffmpegPath">The ffmpeg binary the container is probed with.</param>
    /// <param name="processes">The process layer the probe runs through.</param>
    /// <exception cref="InvalidOperationException">The stream could not be mapped.</exception>
    internal static async Task<(int ContainerIndex, int SubtitleOrdinal, List<string> Codecs)> ResolveContainerSubtitleIndexAsync(
        Video video, MediaBrowser.Model.Entities.MediaStream target, string ffmpegPath, SubSyncProcesses processes)
    {
        var (_, stderr) = await processes.RunProcessArgumentListAsync(ffmpegPath, new[] { "-i", video.Path }, null, CancellationToken.None).ConfigureAwait(false);
        var containerSubs = MediaStreamMap.ParseProbeSubtitleIndexes(stderr);
        var subtitleCodecs = MediaStreamMap.ParseProbeSubtitleCodecs(stderr);

        var mediaSources = video.GetMediaSources(true);
        var jellyfinStreams = (mediaSources.Count > 0 ? mediaSources[0] : null)?.MediaStreams
            ?? new List<MediaBrowser.Model.Entities.MediaStream>();
        var jellyfinEmbedded = jellyfinStreams
            .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !s.IsExternal)
            .ToList();

        // The ordinal comes from the same definition the enqueue path uses, so the track a job was
        // queued for and the track this resolver finds cannot drift apart.
        var pos = MediaStreamMap.EmbeddedSubtitleOrdinal(jellyfinStreams, target);

        if (pos >= 0 && pos < containerSubs.Count && containerSubs.Count == jellyfinEmbedded.Count)
        {
            return (containerSubs[pos], pos, subtitleCodecs);
        }

        throw new InvalidOperationException(
            $"Could not map the embedded subtitle to a real container stream: ffmpeg reports {containerSubs.Count} subtitle stream(s) " +
            $"(container indexes [{string.Join(", ", containerSubs)}]) but Jellyfin reports {jellyfinEmbedded.Count} embedded subtitle stream(s) " +
            $"for {video.Path}.");
    }

    /// <summary>
    /// True when an external subtitle is one of this plugin's own outputs.
    /// </summary>
    /// <remarks>
    /// They are written as <c>&lt;video&gt;.SYNCED.&lt;lang&gt;.srt</c>, and Jellyfin reads the marker
    /// as the language name - so they used to appear in every track list as a language called
    /// "SYNCED" and were queued alongside the very tracks they were produced from. A season sync then
    /// did the same work twice, and the second pass wrote over the file the first had just written.
    /// The originals are still listed; syncing one produces the sidecar again.
    /// </remarks>
    /// <param name="stream">Subtitle stream from Jellyfin's media source.</param>
    /// <returns>True when the file is one of ours.</returns>
    internal static bool IsOwnSidecar(MediaBrowser.Model.Entities.MediaStream stream) =>
        stream.IsExternal && SrtWriter.IsSyncedSidecarName(stream.Path);

    /// <summary>
    /// Position of a subtitle stream among the file's embedded subtitle tracks.
    ///
    /// This is the number everything downstream means by "the subtitle's ordinal": the extraction lane,
    /// the subtitle cache, and ffmpeg's own <c>0:s:N</c>. Jellyfin's <c>MediaStream.Index</c> is not that
    /// number - it counts every stream in the file, video and audio included - so a file whose subtitles
    /// sit behind them has both, differing by the number of streams in front. Passing the index where an
    /// ordinal is expected refused five jobs on a real run ("subtitle ordinal 11 out of range (11
    /// tracks)"), and read a neighbouring track on the files where the index landed inside the range.
    /// </summary>
    /// <param name="streams">The media source's streams.</param>
    /// <param name="target">The subtitle stream that was chosen.</param>
    /// <returns>The 0-based ordinal among embedded subtitle streams, or -1 when there is none.</returns>
    public static int EmbeddedSubtitleOrdinal(
        IEnumerable<MediaBrowser.Model.Entities.MediaStream> streams,
        MediaBrowser.Model.Entities.MediaStream target)
    {
        if (target.IsExternal)
        {
            return -1; // a sidecar file is not one of the file's embedded tracks
        }

        var embedded = streams
            .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !s.IsExternal)
            .OrderBy(s => s.Index)
            .ToList();

        var pos = embedded.FindIndex(s => s.Index == target.Index);
        if (pos < 0 && embedded.Count == 1)
        {
            pos = 0; // single embedded track — safe positional fallback
        }

        return pos;
    }

    /// <summary>
    /// Reads the subtitle position out of an ffmpeg stream specifier such as <c>s:1</c>.
    /// </summary>
    /// <param name="streamSpec">Stream specifier from <see cref="SelectReferenceStream"/>.</param>
    /// <returns>The 0-based position, or -1 when it names something else (audio, or nothing).</returns>
    public static int SubtitleStreamOrdinal(string? streamSpec)
    {
        if (string.IsNullOrWhiteSpace(streamSpec)
            || !streamSpec.StartsWith("s:", StringComparison.Ordinal))
        {
            return -1;
        }

        return int.TryParse(
            streamSpec.AsSpan(2),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var ordinal) && ordinal >= 0
            ? ordinal
            : -1;
    }

    /// <summary>
    /// Parses ffmpeg's "-i" output for subtitle stream codecs, in stream order, so text
    /// tracks can be told apart from image tracks ("Stream #0:4(eng): Subtitle: subrip").
    /// </summary>
    /// <param name="ffmpegOutput">ffmpeg "-i" output.</param>
    /// <returns>Codecs in subtitle-stream order (index 0 = first subtitle stream).</returns>
    public static List<string> ParseProbeSubtitleCodecs(string ffmpegOutput)
    {
        var result = new List<string>();
        foreach (var line in ffmpegOutput.Split('\n'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"Stream\s+#0:\d+[^:]*:\s*Subtitle:\s*(\S+)");
            if (match.Success)
            {
                result.Add(match.Groups[1].Value.Trim());
            }
        }

        return result;
    }

    /// <summary>
    /// Chooses which stream of the media file ffsubsync should derive its speech signal
    /// from, for an embedded subtitle being synced.
    ///
    /// ffsubsync's default detector (<c>subs_then_*</c>) prefers an embedded text subtitle
    /// stream as the speech signal — cheap and accurate — but if the track being synced is
    /// itself an embedded text stream of the same file, "the file's subs" and "the subtitle
    /// we are fixing" are the same track, so the alignment can only return zero and the
    /// subtitle is reported as already in sync. Measured: a track 6 s out of sync came back
    /// unchanged (offset 0.000), while the same run with the reference pointed at the file's
    /// other text track applied exactly -6.000 s.
    ///
    /// So: pick another *text* subtitle stream when the file has one, otherwise fall back to
    /// the audio stream.
    /// </summary>
    /// <param name="isEmbedded">Whether the subtitle being synced came from this file.</param>
    /// <param name="subtitleCodecs">Codecs in subtitle-stream order.</param>
    /// <param name="targetOrdinal">0-based position of the subtitle being synced.</param>
    /// <param name="forcedTracks">Forced flag per subtitle stream, when known.</param>
    /// <returns>An ffmpeg stream specifier ("s:1", "a:0") or null to leave the default.</returns>
    public static string? SelectReferenceStream(
        bool isEmbedded,
        IReadOnlyList<string> subtitleCodecs,
        int targetOrdinal,
        IReadOnlyList<bool>? forcedTracks = null)
    {
        // An external sidecar passes -1: it has no track of its own inside the file, so every embedded
        // text track is a candidate. This used to return null for an external target, which sent every
        // external subtitle to the audio even when the file carried a track that would have made the
        // alignment exact.
        for (var position = 0; position < subtitleCodecs.Count; position++)
        {
            if (position == targetOrdinal)
            {
                continue;
            }

            // A forced/signs track holds a handful of lines over a whole episode. Using one as the
            // reference drags every other track onto it: measured on a real server, a 8-cue signs track
            // moved five full language tracks by the same +57.5 s. Never pick one.
            if (forcedTracks is not null && position < forcedTracks.Count && forcedTracks[position])
            {
                continue;
            }

            if (IsTextSubtitleCodec(subtitleCodecs[position]))
            {
                return "s:" + position.ToString(CultureInfo.InvariantCulture);
            }
        }

        // Only itself (or image tracks): force the audio, otherwise the sync is a no-op.
        return "a:0";
    }

    private static bool IsTextSubtitleCodec(string codec)
    {
        var value = (codec ?? string.Empty).ToLowerInvariant();
        if (LanguageSupport.IsImageBased(value))
        {
            return false;
        }

        return value.Contains("subrip")
            || value.Contains("srt")
            || value.Contains("ass")
            || value.Contains("ssa")
            || value.Contains("webvtt")
            || value.Contains("mov_text")
            || value.Contains("ttml")
            || value.Contains("text");
    }

    /// <summary>
    /// Parses ffmpeg's "-i" output for the container indexes of its subtitle
    /// streams ("Stream #0:4(eng): Subtitle: ...").
    /// </summary>
    public static List<int> ParseProbeSubtitleIndexes(string ffmpegOutput)
    {
        var result = new List<int>();
        foreach (var line in ffmpegOutput.Split('\n'))
        {
            var match = System.Text.RegularExpressions.Regex.Match(line, @"Stream\s+#0:(\d+)[^:]*:\s*Subtitle:");
            if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            {
                result.Add(idx);
            }
        }

        return result;
    }
}
