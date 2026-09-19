using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// What an extraction cost, for logging and for the UI.
/// </summary>
public sealed class MkvExtractionStats
{
    /// <summary>Gets or sets how the subtitles were located.</summary>
    public string Method { get; set; } = "unknown";

    /// <summary>
    /// Gets the requested tracks this pass could not produce (B2).
    /// </summary>
    /// <remarks>
    /// A shared pass returns success while an individual track is missing from its results, and nothing in the
    /// return said which one: on the mixed-index fixture one of two tracks was absent while the call reported
    /// true with an empty reason, so reading the log could not tell whether the missing track was the language
    /// someone was waiting for. The ordinals are listed here, the lane line names them, and the caller can say
    /// so in the job's own note.
    /// </remarks>
    public List<int> MissedTracks { get; } = new();

    /// <summary>Gets or sets how many byte ranges the pass fetched up front.</summary>
    public int PrefetchedRanges { get; set; }

    /// <summary>Gets or sets how many bytes those ranges held.</summary>
    public long PrefetchedBytes { get; set; }

    /// <summary>Gets or sets how many fetched ranges no read ever used.</summary>
    public int PrefetchedUnusedRanges { get; set; }

    /// <summary>Gets or sets how many fetched bytes no read ever used.</summary>
    public long PrefetchedUnusedBytes { get; set; }

    /// <summary>Gets or sets how many bytes this pass asked the file for more than once.</summary>
    public long BytesReadTwice { get; set; }

    /// <summary>Gets or sets the bytes a read went to the file for even though the pass had fetched them.</summary>
    public long BytesReadAfterFetch { get; set; }

    /// <summary>Gets or sets the bytes a fetch covered that the location phase had already read.</summary>
    public long BytesFetchedOverDisk { get; set; }

    /// <summary>Gets or sets how many reads were answered from memory rather than from the file.</summary>
    public int MemoryServedReads { get; set; }

    /// <summary>Gets or sets the bytes the pass's plans expected to read.</summary>
    public long PlanExpectedBytes { get; set; }

    /// <summary>Gets or sets the read calls the pass's plans expected to make.</summary>
    public int PlanExpectedCalls { get; set; }

    /// <summary>Gets or sets how many of the pass's plans missed their own prediction by more than a factor of two.</summary>
    public int PlanMissed { get; set; }

    /// <summary>Gets the expected-versus-actual line of every phase this pass planned.</summary>
    public List<string> PlanLines { get; } = new();

    /// <summary>
    /// Gets or sets how many of those lines report a bound rather than an estimate - a plan whose cost
    /// cannot be known before the work (the walk stops once it has found what it came for). They carry
    /// <see cref="ReadPolicy.BoundNote"/> in the log so the gap is visible instead of looking verified.
    /// </summary>
    public int PlanBound { get; set; }

    /// <summary>Gets or sets the route the pass read by.</summary>
    public string Route { get; set; } = "unknown";

    /// <summary>Gets or sets milliseconds per read as the pass measured the storage.</summary>
    public double MeasuredMsPerRead { get; set; }

    /// <summary>Gets or sets megabytes per second as the pass measured the storage.</summary>
    public double MeasuredMbPerSecond { get; set; }

    /// <summary>Gets or sets how many bytes this extraction asked the file system for.</summary>
    public long BytesRead { get; set; }

    /// <summary>Gets or sets how many read calls were made.</summary>
    public int ReadCalls { get; set; }

    /// <summary>Gets or sets how many clusters were visited.</summary>
    public int ClustersVisited { get; set; }

    /// <summary>Gets or sets how many subtitle blocks were decoded.</summary>
    public int SubtitleBlocks { get; set; }

    /// <summary>
    /// Gets or sets how many further subtitle tracks this pass also produced, from the same read of
    /// the file. Zero means the pass served a single track.
    /// </summary>
    public int AlsoTracks { get; set; }

    /// <summary>Gets or sets how many subtitle blocks those further tracks contributed.</summary>
    public int AlsoBlocks { get; set; }

    /// <summary>Cue points that named the block's position inside its cluster, not just the cluster.</summary>
    public int BlockOffsets { get; set; }

    /// <summary>Fastest measured read on the storage holding the file, in milliseconds.</summary>
    public double StorageProbeMs { get; set; }

    /// <summary>Window the walk used, in bytes, chosen from <see cref="StorageProbeMs"/>.</summary>
    public int WalkWindow { get; set; }

    /// <summary>Tracks a shared pass served by walking a cluster, because their index gave no block position.</summary>
    public int WalkedTracks { get; set; }

    /// <summary>Gets or sets how many cue points the index held (all tracks).</summary>
    public long CuePoints { get; set; }

    /// <summary>Gets or sets how many indexed cue points had to fall back to a cluster walk.</summary>
    public int IndexedMisses { get; set; }

    /// <summary>Gets or sets how many clusters were actually walked, once each, after a located read failed.</summary>
    public int WalkedClusters { get; set; }

    /// <summary>Gets or sets the time spent locating the subtitle data, in milliseconds.</summary>
    public double LocateMs { get; set; }

    /// <summary>Gets or sets the time spent reading it, in milliseconds.</summary>
    public double ReadMs { get; set; }

    /// <summary>Gets or sets bytes the kernel read on this process's behalf, or -1 if unknown.</summary>
    public long KernelBytesRead { get; set; } = -1;

    /// <summary>Gets or sets read syscalls the kernel performed, or -1 if unknown.</summary>
    public long KernelReadCalls { get; set; } = -1;

    /// <summary>Gets or sets average milliseconds per read call.</summary>
    public double ReadLatencyMs { get; set; }

    /// <summary>Gets or sets subtitle blocks decoded per second.</summary>
    public double BlocksPerSecond { get; set; }

    /// <summary>Gets or sets the total time, in milliseconds.</summary>
    public double TotalMs { get; set; }

    /// <summary>Human-readable one-liner for the log.</summary>
    /// <returns>Summary such as "seekhead-cues: 41 clusters, 0.7 MB, 63 ms".</returns>
    public override string ToString() =>
        string.Format(
            CultureInfo.InvariantCulture,
            "{0}: {1} clusters, {2} blocks, {3:0.0} MB in {4} reads, {5} ms (locate {6} ms, read {7} ms, {8} cue points, {9} with a block offset)",
            Method,
            ClustersVisited,
            SubtitleBlocks,
            BytesRead / 1e6,
            ReadCalls,
            TotalMs,
            LocateMs,
            ReadMs,
            CuePoints,
            BlockOffsets)
        + (AlsoTracks > 0
            ? string.Format(
                CultureInfo.InvariantCulture,
                " shared with {0} more track(s), {1} blocks",
                AlsoTracks,
                AlsoBlocks)
            : string.Empty)
        + string.Format(
            CultureInfo.InvariantCulture,
            " | {0:0.0} ms/read, {1:0} blocks/s, kernel {2} bytes in {3} calls",
            ReadLatencyMs,
            // No fallback: FinaliseStats is called after the counters now, so this is the pass's own figure.
            // It used to be recomputed here because the field was always 0 - and a second copy of the sum
            // is what hides the first one being wrong.
            BlocksPerSecond,
            KernelBytesRead,
            KernelReadCalls);
}

/// <summary>
/// Reads text subtitle tracks straight out of a Matroska file, touching as little data as
/// the container allows.
///
/// ffmpeg (and any generic demuxer) walks every cluster looking for a subtitle that takes a
/// few kilobytes, so extraction costs a full-file read — minutes on a large remux. This
/// reader uses the container's own structures instead:
/// <list type="bullet">
/// <item>the SeekHead to jump straight to Tracks and Cues, without walking past clusters;</item>
/// <item>the Cues index (read in one bulk pass) to find the clusters holding the track;</item>
/// <item>block <em>headers</em> only, seeking over every payload that is not this track's —
///   the difference between reading 60 GB of video and reading a few megabytes;</item>
/// <item>a metadata-only cluster scan when the index has no entries for the track, rather
///   than handing a 60 GB file back to ffmpeg.</item>
/// </list>
///
/// Only text subtitle codecs are handled (S_TEXT/UTF8, S_TEXT/ASS, S_TEXT/SSA). Anything it
/// cannot decode with certainty — laced or compressed blocks, unknown codecs, malformed
/// EBML — returns false so the caller can fall back to ffmpeg.
/// </summary>
public static class MkvSubtitleExtractor
{
    private static long _kernelBytesAtStart = -1;
    private static long _kernelCallsAtStart = -1;

    private const ulong IdEbml = 0x1A45DFA3;
    private const ulong IdSegment = 0x18538067;
    private const ulong IdSeekHead = 0x114D9B74;
    private const ulong IdSeek = 0x4DBB;
    private const ulong IdSeekId = 0x53AB;
    private const ulong IdSeekPosition = 0x53AC;
    private const ulong IdTracks = 0x1654AE6B;
    private const ulong IdTrackEntry = 0xAE;
    private const ulong IdTrackNumber = 0xD7;
    private const ulong IdTrackType = 0x83;
    private const ulong IdCodecId = 0x86;
    private const ulong IdCodecPrivate = 0x63A2;
    private const ulong IdContentEncodings = 0x6D80;
    private const ulong IdCues = 0x1C53BB6B;
    private const ulong IdCuePoint = 0xBB;
    private const ulong IdCueTime = 0xB3;
    private const ulong IdCueTrackPositions = 0xB7;
    private const ulong IdCueTrack = 0xF7;
    private const ulong IdCueClusterPosition = 0xF1;
    private const ulong IdCueRelativePosition = 0xF0;
    private const ulong IdCluster = 0x1F43B675;
    private const ulong IdClusterTimecode = 0xE7;
    private const ulong IdSimpleBlock = 0xA3;
    private const ulong IdBlockGroup = 0xA0;
    private const ulong IdBlock = 0xA1;
    private const ulong IdBlockDuration = 0x9B;
    private const ulong IdTimecodeScale = 0x2AD7B1;
    private const ulong IdVoid = 0xEC;
    private const ulong IdCrc32 = 0xBF;

    /// <summary>Largest index element we are willing to pull into memory.</summary>
    private const int MaxIndexBytes = 256 * 1024 * 1024;

    /// <summary>How often the scan reports progress (clusters).</summary>
    private const int ProgressEveryClusters = 400;

    /// <summary>Verbose parser diagnostics on stderr (used by the benchmark harness).</summary>
    internal static bool VerboseDiagnostics { get; set; }

    private static void Diag(string message)
    {
        if (VerboseDiagnostics)
        {
            Console.Error.WriteLine("[mkv] " + message);
        }
    }

    /// <summary>Cheap pre-check: is this file a Matroska container?</summary>
    /// <param name="path">Candidate media path.</param>
    /// <returns>True when the extension and magic bytes look like EBML/Matroska.</returns>
    public static bool LooksLikeMatroska(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return false;
            }

            var extension = Path.GetExtension(path);
            if (!extension.Equals(".mkv", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".mka", StringComparison.OrdinalIgnoreCase)
                && !extension.Equals(".webm", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            Span<byte> magic = stackalloc byte[4];
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return stream.Read(magic) == 4
                && magic[0] == 0x1A && magic[1] == 0x45 && magic[2] == 0xDF && magic[3] == 0xA3;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Extracts one embedded text subtitle track to SRT text.
    /// </summary>
    /// <param name="videoPath">Path of the media file (must be Matroska).</param>
    /// <param name="subtitleOrdinal">
    /// 0-based index among the file's subtitle tracks, matching ffmpeg's <c>0:s:N</c>.
    /// </param>
    /// <param name="srtText">The extracted subtitles when the method returns true.</param>
    /// <param name="reason">Why extraction was skipped, when it returns false.</param>
    /// <returns>True when a complete SRT was produced.</returns>
    public static bool TryExtract(string videoPath, int subtitleOrdinal, out string srtText, out string reason) =>
        TryExtract(videoPath, subtitleOrdinal, out srtText, out reason, null, out _);

    /// <summary>
    /// Extracts one embedded text subtitle track to SRT text, reporting progress as it goes.
    /// </summary>
    /// <param name="videoPath">Path of the media file (must be Matroska).</param>
    /// <param name="subtitleOrdinal">
    /// 0-based index among the file's subtitle tracks, matching ffmpeg's <c>0:s:N</c>.
    /// </param>
    /// <param name="srtText">The extracted subtitles when the method returns true.</param>
    /// <param name="reason">Why extraction was skipped, when it returns false.</param>
    /// <param name="progress">Called with a short status line while the file is being read.</param>
    /// <param name="stats">What the extraction cost.</param>
    /// <param name="alsoExtract">
    /// Further subtitle tracks of the same file to produce from this same read. They land in
    /// <paramref name="alsoResults"/>; the track above is still the one that decides success.
    /// </param>
    /// <param name="alsoResults">Extracted SRT text per ordinal for those further tracks.</param>
    /// <param name="cancellationToken">Cancels a long read (Kill in the UI).</param>
    /// <param name="publish">
    /// Called with (ordinal, SRT) for each track the moment the reading has passed its last line, so
    /// a caller can start work on that subtitle while the rest of the file is still being read.
    /// </param>
    /// <returns>True when a complete SRT was produced.</returns>
    public static bool TryExtract(
        string videoPath,
        int subtitleOrdinal,
        out string srtText,
        out string reason,
        Action<string>? progress,
        out MkvExtractionStats stats,
        IReadOnlyList<int>? alsoExtract = null,
        Dictionary<int, string>? alsoResults = null,
        CancellationToken cancellationToken = default,
        Action<int, string>? publish = null)
    {
        srtText = string.Empty;
        reason = string.Empty;
        stats = new MkvExtractionStats();
        var watch = Stopwatch.StartNew();

        BlobReader? reader = null;
        try
        {
            // bufferSize 0: every seek must cost the bytes it actually reads, not a 64 KB
            // buffered refill. Without this, a metadata walk of a 60 GB file reads the file.
            using var stream = new FileStream(videoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 0);
            // The path goes in with the stream (P5-9): the ReadPolicy this pass builds is only handed a
            // volume profile when the reader knows what file it is reading, and without one every read it
            // measures is dropped - the volume keeps the one probe it took instead of learning from the
            // reads the pass was making anyway.
            reader = new BlobReader(stream, videoPath);
            var result = Extract(
                reader,
                System.IO.Path.GetFileName(videoPath),
                subtitleOrdinal,
                progress,
                out srtText,
                out reason,
                stats,
                alsoExtract,
                alsoResults,
                cancellationToken,
                publish);
            stats.BytesRead = reader.BytesRead;

            // Counters first, derived figures second: FinaliseStats computes ms/read and blocks/s from
            // ReadCalls and TotalMs, so calling it before they are assigned left the summary's block rate
            // at 0 on every extraction (ReadLatencyMs survived only because the cue-indexed loop's live
            // counter line fills ReadCalls as it goes).
            stats.ReadCalls = reader.ReadCalls;
            stats.TotalMs = watch.Elapsed.TotalMilliseconds;
            FinaliseStats(stats);
            return result;
        }
        catch (Exception ex)
        {
            Diag(ex.ToString());
            reason = ex.GetType().Name + ": " + ex.Message;
            stats.Method = "failed";
            stats.BytesRead = reader?.BytesRead ?? 0;

            // Same order as the success path, for the same reason: the derived figures must see the
            // counters the pass stopped with, failure or not.
            stats.ReadCalls = reader?.ReadCalls ?? 0;
            stats.TotalMs = watch.Elapsed.TotalMilliseconds;
            FinaliseStats(stats);
            return false;
        }
    }

    /// <summary>
    /// Extracts several embedded text subtitle tracks of one file in a single pass over it.
    /// </summary>
    /// <remarks>
    /// Reading a file's clusters once and taking every requested track out of them is what makes a
    /// multi-language episode affordable: the alternative visits the same clusters once per track.
    /// Tracks this pass cannot serve (no index entries, no block offsets, not text) are left out of
    /// <paramref name="results"/> so the caller can extract those on their own.
    /// </remarks>
    /// <param name="videoPath">Path of the media file (must be Matroska).</param>
    /// <param name="subtitleOrdinals">
    /// 0-based ordinals among the file's subtitle tracks, as ffmpeg's <c>0:s:N</c>.
    /// </param>
    /// <param name="results">Extracted SRT text per ordinal, for the tracks that were produced.</param>
    /// <param name="reason">Why nothing could be extracted, when it returns false.</param>
    /// <param name="stats">What the pass cost (including the extra tracks).</param>
    /// <param name="cancellationToken">Cancels a long read (Kill in the UI).</param>
    /// <param name="publish">
    /// Called with (ordinal, SRT) for each track the moment the reading has passed its last line.
    /// </param>
    /// <returns>True when at least one requested track was extracted.</returns>
    public static bool TryExtractMany(
        string videoPath,
        IReadOnlyList<int> subtitleOrdinals,
        out Dictionary<int, string> results,
        out string reason,
        out MkvExtractionStats stats,
        CancellationToken cancellationToken = default,
        Action<int, string>? publish = null)
    {
        results = new Dictionary<int, string>();
        stats = new MkvExtractionStats();
        if (subtitleOrdinals.Count == 0)
        {
            reason = "no subtitle tracks requested";
            return false;
        }

        var primary = subtitleOrdinals[0];
        var extras = subtitleOrdinals.Where(o => o != primary).ToList();
        var ok = TryExtract(
            videoPath,
            primary,
            out var primaryText,
            out reason,
            null,
            out stats,
            extras,
            results,
            cancellationToken,
            publish);

        if (ok && primaryText.Length > 0)
        {
            results[primary] = primaryText;
            publish?.Invoke(primary, primaryText);
        }

        if (results.Count == 0)
        {
            stats.MissedTracks.AddRange(subtitleOrdinals);
            return false;
        }

        // Every requested track that is not in the results is named (B2), whether the pass failed for it or
        // simply never produced it: success with a silent gap is the shape this row is about.
        foreach (var requested in subtitleOrdinals)
        {
            if (!results.TryGetValue(requested, out var text) || string.IsNullOrEmpty(text))
            {
                stats.MissedTracks.Add(requested);
            }
        }

        if (!ok)
        {
            reason = $"track {primary} failed ({reason}); {results.Count} other track(s) extracted";
        }

        if (stats.MissedTracks.Count > 0)
        {
            var missed = string.Join(", ", stats.MissedTracks);
            reason = string.IsNullOrEmpty(reason)
                ? $"no subtitle for track(s) {missed}"
                : $"{reason}; no subtitle for track(s) {missed}";
        }

        return true;
    }

    private static bool Extract(
        BlobReader reader,
        string label,
        int subtitleOrdinal,
        Action<string>? progress,
        out string srtText,
        out string reason,
        MkvExtractionStats stats,
        IReadOnlyList<int>? alsoExtract,
        Dictionary<int, string>? alsoResults,
        CancellationToken cancellationToken = default,
        Action<int, string>? publish = null)
    {
        srtText = string.Empty;
        reason = string.Empty;

        // One policy per pass. It is given the file's length and the route this pass starts by, and from
        // then on it is the only thing that decides a window, a fetch or a route: the cue-indexed loop, the
        // cluster walk and the shared multi-track pass all ask it and all follow what it returns.
        var policy = new ReadPolicy(
            reader.Length,
            ReadRoute.CueIndexed,
            label,
            string.IsNullOrEmpty(reader.Path) ? null : VolumeProfiles.For(reader.Path));
        reader.Policy = policy;

        // --- EBML header, then the Segment ---
        if (!reader.TryReadElementHeaderAt(0, out var id, out var size, out var headerLength) || id != IdEbml)
        {
            reason = "not EBML";
            return false;
        }

        var segmentHeaderPosition = headerLength + (long)size;
        if (!reader.TryReadElementHeaderAt(segmentHeaderPosition, out id, out size, out headerLength)
            || id != IdSegment)
        {
            reason = "no Segment";
            return false;
        }

        var segmentDataStart = segmentHeaderPosition + headerLength;
        var segmentDataEnd = size == ulong.MaxValue ? reader.Length : segmentDataStart + (long)size;
        var searchEnd = Math.Min(segmentDataEnd, reader.Length);

        var locateWatch = Stopwatch.StartNew();

        // --- 1. SeekHead: ask the file where its index is, instead of walking to it ---
        long tracksDataStart = -1;
        long tracksSize = 0;
        long cuesDataStart = -1;
        long cuesSize = 0;
        var firstClusterPosition = -1L;
        var seekheadHit = false;

        var header = reader.TryReadElementHeaderAt(segmentDataStart, out var firstId, out var firstSize, out var firstLength);
        if (header && firstId == IdSeekHead && firstSize <= MaxIndexBytes)
        {
            var payload = reader.ReadRegion(segmentDataStart + (long)firstLength, (long)firstSize);
            var entries = ParseSeekHead(payload);
            Diag($"seekhead at {segmentDataStart}, {entries.Count} entries, segmentDataStart={segmentDataStart}");
            foreach (var (seekId, seekPosition) in entries)
            {
                var absolute = segmentDataStart + seekPosition;
                Diag($"  entry 0x{seekId:X} -> {absolute} (relative {seekPosition})");
                if (seekId == IdCues)
                {
                    cuesDataStart = absolute;
                    seekheadHit = true;
                }
                else if (seekId == IdTracks)
                {
                    tracksDataStart = absolute;
                }
            }
        }

        // SeekHead positions point at element headers; read each one to get its payload.
        if (cuesDataStart > 0 && !TryResolveElement(reader, cuesDataStart, IdCues, out cuesDataStart, out cuesSize))
        {
            Diag($"cues offset {cuesDataStart} is not a Cues element");
            cuesDataStart = -1;
        }

        if (tracksDataStart > 0 && !TryResolveElement(reader, tracksDataStart, IdTracks, out tracksDataStart, out tracksSize))
        {
            Diag($"tracks offset {tracksDataStart} is not a Tracks element");
            tracksDataStart = -1;
        }

        // --- 2. Anything the SeekHead did not cover: walk element headers only ---
        if (tracksDataStart < 0 || cuesDataStart < 0 || firstClusterPosition < 0)
        {
            var cursor = segmentDataStart;
            while (cursor < searchEnd)
            {
                if (!reader.TryReadElementHeaderAt(cursor, out var walkId, out var walkSize, out var walkLength))
                {
                    break;
                }

                var walkData = cursor + walkLength;
                if (walkId == IdSeekHead && walkSize <= MaxIndexBytes)
                {
                    foreach (var (seekId, seekPosition) in ParseSeekHead(reader.ReadRegion(walkData, (long)walkSize)))
                    {
                        var absolute = segmentDataStart + seekPosition;
                        if (seekId == IdCues && cuesDataStart < 0
                            && TryResolveElement(reader, absolute, IdCues, out var cuesFromSeek, out var sizeFromSeek))
                        {
                            cuesDataStart = cuesFromSeek;
                            cuesSize = sizeFromSeek;
                        }
                        else if (seekId == IdTracks && tracksDataStart < 0
                            && TryResolveElement(reader, absolute, IdTracks, out var tracksFromSeek, out var trackSizeFromSeek))
                        {
                            tracksDataStart = tracksFromSeek;
                            tracksSize = trackSizeFromSeek;
                        }
                    }
                }
                else if (walkId == IdCluster)
                {
                    if (firstClusterPosition < 0)
                    {
                        firstClusterPosition = cursor;
                    }

                    if (cuesDataStart >= 0 && tracksDataStart >= 0)
                    {
                        break; // everything we need is known
                    }
                }
                else if (walkId == IdTracks && tracksDataStart < 0)
                {
                    tracksDataStart = walkData;
                    tracksSize = (long)walkSize;
                }
                else if (walkId == IdCues && cuesDataStart < 0)
                {
                    cuesDataStart = walkData;
                    cuesSize = (long)walkSize;
                }

                if (walkSize == ulong.MaxValue)
                {
                    cursor = walkData; // unknown size (streamed): step inside and keep looking
                    continue;
                }

                cursor = walkData + (long)walkSize;
            }
        }

        if (firstClusterPosition < 0)
        {
            firstClusterPosition = segmentDataStart;
        }

        if (tracksDataStart < 0)
        {
            Diag($"no Tracks element (walked from {segmentDataStart} to {searchEnd}, firstCluster={firstClusterPosition})");
            reason = "no Tracks element";
            return false;
        }

        Diag($"layout: tracks={tracksDataStart}, cues={cuesDataStart}, firstCluster={firstClusterPosition}");

        // --- 3. Which subtitle track is wanted? ---
        if (tracksSize <= 0 || tracksSize > MaxIndexBytes)
        {
            reason = "implausible Tracks size";
            return false;
        }

        Diag($"tracks element at {tracksDataStart}, size {tracksSize}");
        var tracks = ParseTrackEntries(reader.ReadRegion(tracksDataStart, tracksSize));
        var subtitleTracks = tracks.Where(t => t.IsSubtitle).OrderBy(t => t.TrackNumber).ToList();
        if (subtitleTracks.Count == 0)
        {
            reason = "no subtitle tracks";
            return false;
        }

        if (subtitleOrdinal < 0 || subtitleOrdinal >= subtitleTracks.Count)
        {
            reason = $"subtitle ordinal {subtitleOrdinal} out of range ({subtitleTracks.Count} tracks)";
            return false;
        }

        var track = subtitleTracks[subtitleOrdinal];
        if (!track.IsText)
        {
            reason = $"codec {track.CodecId} is not text";
            return false;
        }

        if (track.Compressed)
        {
            reason = "compressed subtitle blocks";
            return false;
        }

        // --- 4. Where does that track live? Cue index first, scan otherwise ---
        var cues = new List<Cue>();
        List<CueRef> cueRefs = new();
        byte[]? cueBuffer = null;

        if (cuesDataStart > 0 && cuesSize > 0 && cuesSize <= MaxIndexBytes)
        {
            cueBuffer = reader.ReadRegion(cuesDataStart, cuesSize);
            cueRefs = ParseCueRefs(cueBuffer, track.TrackNumber, out var cuePoints);
            stats.CuePoints = cuePoints;
        }

        locateWatch.Stop();
        stats.LocateMs = locateWatch.Elapsed.TotalMilliseconds;
        Diag($"located: {cueRefs.Count} cue points for track {track.TrackNumber} "
            + $"({cueRefs.Count(r => r.RelativePosition >= 0)} with a block offset), method pending");

        var readWatch = Stopwatch.StartNew();
        MarkIoBaseline();
        var (kernelBytesAtStart, kernelCallsAtStart) = (_kernelBytesAtStart, _kernelCallsAtStart);

        // Cue points that name a cluster but not the block inside it are a trap: finding the block
        // means walking that cluster's ~150 blocks, and there is one such cluster per cue point, so
        // the walk ends up covering the whole file (measured: 2,069 clusters, 611 MB, 10,779 reads for
        // one 611 MB episode). What made it expensive was the window it walked with (64 KB, so ~5 reads
        // per cluster and the windows tiled the file: 10,779 reads, 611 MB, 42 s measured), not the
        // decision to use the index. Both shapes are now priced by the read policy from what the index
        // provides, and the block offsets are counted so the log says which shape the file has.
        var blockOffsets = cueRefs.Count(r => r.RelativePosition >= 0);
        stats.BlockOffsets = blockOffsets;
        Diag($"cue refs: {cueRefs.Count}, with block offsets: {blockOffsets}");

        if (cueRefs.Count > 0)
        {
            stats.Method = seekheadHit ? "seekhead-cues" : "cue-index";
            stats.Route = "cue-indexed";

            // The cue index names every block and they are in file order, so their reads can go out
            // together rather than one after another: measured on the user's NAS a read costs 12,8 ms
            // whatever its size, and this loop used to issue one per cue - ~1 500 of them for an episode,
            // about 19 s of waiting for a few hundred KB of text. The plan states which reads those are,
            // so the fetch is made for exactly the reads that follow and the ledger can prove it.
            var located = new List<(long ClusterStart, long BlockStart)>();
            var toWalk = new List<long>();
            foreach (var cueRef in cueRefs)
            {
                var cueCluster = segmentDataStart + cueRef.ClusterOffset;
                if (cueCluster < 0 || cueCluster >= reader.Length)
                {
                    continue;
                }

                if (cueRef.RelativePosition >= 0)
                {
                    // The offset is relative to the cluster's data start, which begins after the cluster's
                    // element header; the block read starts a little before the block for that reason.
                    located.Add((cueCluster, cueCluster + cueRef.RelativePosition + 32));
                }
                else
                {
                    toWalk.Add(cueCluster);
                }
            }

            var cuePlan = policy.PlanIndexedReads(located, toWalk, $"the index locates {blockOffsets} of {cueRefs.Count} cue point(s)");
            // The counters are taken before the plan is applied: applying it is what fetches the ranges the
            // plan priced, so a phase that leaves them out of its own actual cost reads "0 MB, 0 reads" and
            // is then flagged as missing a prediction it in fact met exactly.
            var (cueBytesBefore, cueCallsBefore) = (reader.BytesRead, reader.ReadCalls);
            reader.Apply(cuePlan, cancellationToken);
            PluginLog.Info("extract plan: " + cuePlan.Describe());

            // The pair itself, not a hash of it. The key used to be `position * 31 + relative`, which
            // collides for two cue points whose cluster positions are d bytes apart and whose relative
            // offsets differ by exactly -31d: the second cue point was then skipped without its cluster
            // ever being read, and its subtitle was lost silently (reproduced with
            // tests/fixtures/make_collision.py - clusters 67 bytes apart, one cue point carrying a relative
            // offset of 31 * 67 that points outside its own cluster, the next one sitting at relative 0).
            // A foreign or damaged cue index is exactly where such a pair comes from, and this project has
            // met two of those already.
            var seen = new HashSet<(long ClusterPosition, long RelativePosition)>();
            var index = 0;
            var missedCuePoints = 0;

            // Clusters this pass has already walked whole. A walk reads every block of the wanted track
            // inside its cluster, so a later cue point naming the same cluster has nothing left to find -
            // walking it again is what turned one located block into ~50 reads, once per cue point (S26).
            var walkedClusters = new HashSet<long>();

            // Where an unknown-size cluster ends is not in the file. The next cluster the index mentions
            // starts after this one does, and the last one is bounded by the end of the file, so this is
            // the tightest bound available; ReadIndexedBlock treats it as a bound, not as a fact.
            var clusterStarts = cueRefs.Select(r => r.ClusterOffset).Distinct().OrderBy(o => o).ToArray();
            long ClusterBound(long clusterOffset)
            {
                var at = Array.BinarySearch(clusterStarts, clusterOffset);
                at = at < 0 ? ~at : at + 1;
                return at < clusterStarts.Length ? segmentDataStart + clusterStarts[at] : reader.Length;
            }
            foreach (var cueRef in cueRefs)
            {
                var position = segmentDataStart + cueRef.ClusterOffset;
                if (position < 0 || position >= reader.Length
                    || !seen.Add((position, cueRef.RelativePosition)))
                {
                    continue;
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    reason = "cancelled";
                    stats.Method = "cancelled";
                    return false;
                }

                index++;

                // Live counters: these used to be filled in only after the extraction finished,
                // so the line read "0.0 MB, 0 reads" while it was reading.
                stats.BytesRead = reader.BytesRead;
                stats.ReadCalls = reader.ReadCalls;

                if (progress is not null && (index % 16 == 0 || index == cueRefs.Count))
                {
                    var elapsedMs = readWatch.Elapsed.TotalMilliseconds;
                    stats.ReadLatencyMs = elapsedMs / Math.Max(1, stats.ReadCalls);
                    stats.BlocksPerSecond = stats.ReadLatencyMs <= 0
                        ? 0
                        : index / (elapsedMs / 1000.0);
                    var (kernelBytesNow, kernelCallsNow) = KernelIo();
                    if (kernelBytesNow >= 0 && kernelBytesAtStart >= 0)
                    {
                        stats.KernelBytesRead = kernelBytesNow - kernelBytesAtStart;
                        stats.KernelReadCalls = kernelCallsNow - kernelCallsAtStart;
                        progress(string.Format(
                            CultureInfo.InvariantCulture,
                            "reading subtitle {0}/{1} · {2:0.0} MB, {3} reads · {4:0.0} ms/read · {5:0} cues/s",
                            index,
                            cueRefs.Count,
                            stats.KernelBytesRead / 1e6,
                            stats.KernelReadCalls,
                            stats.ReadLatencyMs,
                            stats.BlocksPerSecond));
                    }
                    else
                    {
                        progress(string.Format(
                            CultureInfo.InvariantCulture,
                            "reading subtitle {0}/{1} · {2:0.0} MB, {3} reads · {4:0.0} ms/read · {5:0} cues/s",
                            index,
                            cueRefs.Count,
                            stats.BytesRead / 1e6,
                            stats.ReadCalls,
                            stats.ReadLatencyMs,
                            stats.BlocksPerSecond));
                    }
                }

                if (walkedClusters.Contains(position))
                {
                    // An earlier cue point in this cluster could not use the index and walked the cluster,
                    // which read every block of this track in it - including this cue point's. There is
                    // nothing left here, and walking it again is the cost this fix exists to remove.
                    continue;
                }

                stats.ClustersVisited++;

                var blocksBefore = track.BlocksFound;

                if (cueRef.RelativePosition >= 0)
                {
                    // The muxer recorded exactly where the block sits inside the cluster, so the plan's
                    // reads replace walking that cluster's ~150 blocks to find it, and the window is the
                    // one the plan priced - never a walk-sized window, which leaked into this loop once and
                    // moved 3.2 GB to collect ~50 KB of text (779 cues, a 4 MB window each).
                    if (ReadIndexedBlock(reader, position, ClusterBound(cueRef.ClusterOffset), cueRef, track, cues, stats)
                        && track.BlocksFound != blocksBefore)
                    {
                        continue;
                    }

                    stats.IndexedMisses++;
                }

                if (!ReadCluster(reader, position, track, cues, stats, policy, wideWindow: true))
                {
                    reason = "unsupported block encoding";
                    return false;
                }

                stats.WalkedClusters++;
                walkedClusters.Add(position);

                // Neither the block the index names nor the walk over its cluster produced a block of
                // this track: the cue point exists in the index but nothing was read for it, so the
                // subtitle that would come out of this pass is short by that much. Counting it here
                // (rather than comparing the cue count with the index's, which cannot tell a missing
                // block from a deliberately blank one) is what turns a silently truncated subtitle
                // into a refusal, so the caller falls back to ffmpeg and gets the whole thing.
                if (track.BlocksFound == blocksBefore)
                {
                    missedCuePoints++;
                }
            }

            var (cueLine, cueMissed) = policy.Compare(cuePlan, reader.BytesRead - cueBytesBefore, reader.ReadCalls - cueCallsBefore);
            stats.PlanLines.Add(cueLine);
            stats.PlanBound += cuePlan.Coarse ? 1 : 0;
            stats.PlanExpectedBytes += cuePlan.ExpectedBytes;
            stats.PlanExpectedCalls += cuePlan.ExpectedCalls;
            stats.PlanMissed += cueMissed ? 1 : 0;
            if (cueMissed)
            {
                PluginLog.Warn(cueLine + " - this pass missed its own prediction");
            }
            else
            {
                PluginLog.Info(cueLine);
            }

            if (missedCuePoints > 0)
            {
                Diag($"incomplete: {missedCuePoints} of {cueRefs.Count} cue points produced no block");
                reason = string.Format(
                    CultureInfo.InvariantCulture,
                    "{0} of {1} cue points in the index name a block that could not be read "
                    + "({2} cue points carried a block offset, {3} did not)",
                    missedCuePoints,
                    cueRefs.Count,
                    blockOffsets,
                    cueRefs.Count - blockOffsets);
                stats.Method = "incomplete";
                return false;
            }
        }
        else
        {
            // No index entries for this track. Walking cluster *headers* is still cheap:
            // payloads are skipped, so a 60 GB remux costs a few megabytes of reads, where
            // handing it back to ffmpeg would cost all 60 GB.
            stats.Method = "metadata-scan";
            stats.Route = "cluster-walk";

            // A metadata walk reads every cluster's block headers and skips the payloads, so the bytes it
            // can avoid depend on the window: a small window reads a fraction of the region with one read
            // per block, a 4 MB window reads all of it with one read per 4 MB. Which is cheaper is the
            // storage's business, so the plan prices it and the walk re-prices itself from its own reads
            // as it goes - never from a probe taken before the work started.
            var walkPlan = policy.PlanWalk(firstClusterPosition, searchEnd, 0);
            var (walkBytesBefore, walkCallsBefore) = (reader.BytesRead, reader.ReadCalls);
            reader.Apply(walkPlan, cancellationToken);
            PluginLog.Info("extract plan: " + walkPlan.Describe());
            if (!ScanClusters(
                    reader, firstClusterPosition, searchEnd, track, cues, stats, progress, policy,
                    kernelCallsAtStart, cancellationToken))
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    reason = "cancelled";
                    stats.Method = "cancelled";
                    return false;
                }

                reason = "unsupported block encoding";
                return false;
            }

            var (walkLine, walkMissed) = policy.Compare(walkPlan, reader.BytesRead - walkBytesBefore, reader.ReadCalls - walkCallsBefore);
            stats.PlanLines.Add(walkLine);
            stats.PlanBound += walkPlan.Coarse ? 1 : 0;
            stats.PlanExpectedBytes += walkPlan.ExpectedBytes;
            stats.PlanExpectedCalls += walkPlan.ExpectedCalls;
            stats.PlanMissed += walkMissed ? 1 : 0;
            PluginLog.Info(walkLine);
        }

        // --- 4b. Other subtitle tracks of the same file, in the same pass -----------------
        // A file with fifty languages would otherwise pay the cluster reads fifty times. The cue
        // index is already in memory, so every requested track's cue points come out of it for
        // free; the clusters are then visited once in file order, with one header read per cluster
        // and the blocks of every requested track read from the same window. The primary track
        // above is extracted exactly as before: this only adds tracks that were asked for, and a
        // track that cannot be served here is simply left out so the caller can do it alone.
        if (alsoExtract is { Count: > 0 } && alsoResults is not null && cueBuffer is not null)
        {
            var wanted = new List<(int Ordinal, SubtitleTrack Track, List<Cue> Cues)>();
            var expectedCues = new Dictionary<int, int>();
            var clusterPositionsOf = new List<long>();
            var published = new HashSet<int>();

            // Clusters to visit because a wanted block sits in them (with a known position), and
            // clusters to visit because a wanted track has a cue in them but the index does not say
            // where its block is. The second kind used to be left out of the pass entirely - the job
            // then read the file again on its own, which is how one episode cost two full passes
            // (logged as tracks=24/30).
            var byCluster = new SortedDictionary<long, List<(int Index, CueRef Ref)>>();
            var walkInCluster = new SortedDictionary<long, List<int>>();

            // Where each wanted track's last cue sits. A track is complete once the walk has passed
            // that cluster, which is what lets its job start syncing while the pass is still reading
            // the rest of the file for the other languages - the difference between "wait for all 30"
            // and "start the one that is out".
            var lastClusterOf = new SortedDictionary<long, List<int>>();

            foreach (var ordinal in alsoExtract)
            {
                if (ordinal == subtitleOrdinal || ordinal < 0 || ordinal >= subtitleTracks.Count)
                {
                    continue;
                }

                var extra = subtitleTracks[ordinal];
                if (!extra.IsText || extra.Compressed || extra.TrackNumber == track.TrackNumber)
                {
                    continue;
                }

                var extraRefs = ParseCueRefs(cueBuffer, extra.TrackNumber, out _);
                if (extraRefs.Count == 0)
                {
                    continue; // no index entries for it: the caller extracts that one on its own
                }

                var index = wanted.Count;
                wanted.Add((ordinal, extra, new List<Cue>()));

                // Any cue point without a block offset means the track has to be walked: the cue
                // points that do carry one are read directly, but the others can only be found by
                // reading their cluster. Requiring *every* cue point to lack an offset (what this
                // used to say) left the mixed case - which is exactly what a partially rewritten
                // cue index produces - with those cue points skipped and never added to
                // walkInCluster, so the track came out short and was still reported as produced.
                var needsWalk = extraRefs.Any(r => r.RelativePosition < 0);
                expectedCues[index] = extraRefs.Count;
                foreach (var reference in extraRefs)
                {
                    var clusterPosition = segmentDataStart + reference.ClusterOffset;

                    if (reference.RelativePosition < 0)
                    {
                        // This track's index names the cluster but not the block, so the block has to be
                        // found by reading the cluster - which this pass is about to read anyway.
                        if (needsWalk && clusterPosition >= 0 && clusterPosition < reader.Length)
                        {
                            if (!walkInCluster.TryGetValue(clusterPosition, out var walkers))
                            {
                                walkers = new List<int>();
                                walkInCluster[clusterPosition] = walkers;
                            }

                            if (!walkers.Contains(index))
                            {
                                walkers.Add(index);
                            }
                        }

                        continue;
                    }
                    if (clusterPosition < 0 || clusterPosition >= reader.Length)
                    {
                        continue;
                    }

                    if (!byCluster.TryGetValue(clusterPosition, out var bucket))
                    {
                        bucket = new List<(int Index, CueRef Ref)>();
                        byCluster[clusterPosition] = bucket;
                    }

                    bucket.Add((index, reference));
                }

                if (clusterPositionsOf.Count == 0)
                {
                    clusterPositionsOf = new List<long>();
                }

                clusterPositionsOf.AddRange(extraRefs
                    .Where(r => r.RelativePosition >= 0 || needsWalk)
                    .Select(r => segmentDataStart + r.ClusterOffset)
                    .Where(pos => pos >= 0 && pos < reader.Length));

                if (clusterPositionsOf.Count > 0)
                {
                    var last = clusterPositionsOf.Max();
                    if (!lastClusterOf.TryGetValue(last, out var finishers))
                    {
                        finishers = new List<int>();
                        lastClusterOf[last] = finishers;
                    }

                    if (!finishers.Contains(index))
                    {
                        finishers.Add(index);
                    }
                }

                clusterPositionsOf.Clear();
            }

            stats.ClustersVisited += byCluster.Count;

            // Fetch what the walk is about to need, all at once, and exactly those reads: one round trip
            // per cluster is what makes this slow on network storage - 12,8 ms per read on the user's NAS,
            // ~2 000 clusters per episode, ~26 s of the 61 s pass spent waiting between reads - and the walk
            // knows its whole route before it starts. The plan states the reads (the cluster head and the
            // block the index named) so the fetch covers them; a range that does not cover a read it was made
            // for is work the pass pays for and does not use, which is what the ledger exists to catch.
            var clusterIndexed = new List<(long ClusterStart, long BlockStart)>();
            foreach (var (clusterPosition, bucket) in byCluster)
            {
                foreach (var (_, reference) in bucket)
                {
                    if (reference.RelativePosition >= 0)
                    {
                        clusterIndexed.Add((clusterPosition, clusterPosition + reference.RelativePosition + 32));
                    }
                }
            }

            var walkClusters = new List<(long Start, long End)>();
            foreach (var (clusterPosition, _) in walkInCluster)
            {
                walkClusters.Add((clusterPosition, clusterPosition + ReadPolicy.MaxWindow));
            }

            var sharedPlan = policy.PlanSharedReads(
                clusterIndexed,
                walkClusters,
                $"one pass serves {wanted.Count} more track(s) over {byCluster.Count} cluster(s)");
            var (sharedBytesBefore, sharedCallsBefore) = (reader.BytesRead, reader.ReadCalls);
            reader.Apply(sharedPlan, cancellationToken);
            PluginLog.Info("extract plan: " + sharedPlan.Describe());

            foreach (var (clusterPosition, bucket) in byCluster)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!TryReadClusterHeader(reader, clusterPosition, reader.Length, out var extraDataStart, out var extraDataEnd, out _)
                    || !TryReadClusterTimecode(reader, extraDataStart, extraDataEnd, out var extraTimecode))
                {
                    continue;
                }

                // The cue index says where every wanted block sits, so the blocks in this cluster are a
                // known byte range. Reading that range once covers all of them; hunting through the
                // cluster in fixed windows reads the same bytes several times over (measured: 10,782
                // reads for 2,069 clusters, 42 s on a share where a read costs milliseconds).
                var lowest = long.MaxValue;
                var highest = 0L;
                foreach (var (_, reference) in bucket)
                {
                    lowest = Math.Min(lowest, reference.RelativePosition);
                    highest = Math.Max(highest, reference.RelativePosition);
                }

                if (lowest != long.MaxValue)
                {
                    // Plus room for the last block's own header and payload (a subtitle block is a few
                    // hundred bytes) and never more than the file itself. The plan's fetch covers these
                    // reads; this window only says what a read that missed the fetch costs.
                    var span = (highest - lowest) + ReadPolicy.BlockRead;
                    reader.SetWindow(policy.ClusterWindow(span));
                }

                if (walkInCluster.TryGetValue(clusterPosition, out var walkers))
                {
                    // A track here can only be found by reading the cluster, so the window has to cover
                    // it; the walk below then costs parsing, not further reads.
                    reader.SetWindow(policy.ClusterWindow(extraDataEnd - extraDataStart));

                    // A cancelled pass must not start a fresh 16 MB read (B7): the largest single read the
                    // extractor makes is exactly the one worth refusing when the user has asked it to stop.
                    if (cancellationToken.IsCancellationRequested)
                    {
                        break;
                    }

                    // And the walk runs forward through the file, so bring in a whole chunk at once: the
                    // clusters after this one are inside it, which turns a round trip per cluster into a
                    // round trip per chunk.
                    reader.SetSequentialChunk(clusterPosition, ReadPolicy.WalkChunkBytes);
                    foreach (var index in walkers)
                    {
                        var walked = wanted[index];
                        if (ReadClusterChildren(reader, extraDataStart, extraDataEnd, walked.Track, walked.Cues, stats))
                        {
                            stats.WalkedTracks++;
                        }
                    }
                }

                foreach (var (index, reference) in bucket)
                {
                    var target = wanted[index];
                    ReadBlockAtRelative(
                        reader,
                        extraDataStart,
                        extraDataEnd,
                        reference.RelativePosition,
                        extraTimecode,
                        target.Track,
                        target.Cues);
                }

                PublishFinishedTracks(lastClusterOf, clusterPosition, wanted, expectedCues, published, publish);
            }

            for (var i = 0; i < wanted.Count; i++)
            {
                var (ordinal, trackOf, extraCues) = wanted[i];
                if (extraCues.Count == 0)
                {
                    continue;
                }

                // Cue parity: every cue point of this track has to have produced a block, or the
                // subtitle is short and must not be handed over as if it were whole. Reporting the
                // track as produced is what let a half-read language be cached and reused.
                if (expectedCues.TryGetValue(i, out var expected) && trackOf.BlocksFound < expected)
                {
                    Diag($"track {ordinal}: {trackOf.BlocksFound} blocks for {expected} cue points - not published");
                    continue;
                }

                extraCues.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
                var extraText = ToSrt(extraCues);
                if (extraText.Length > 0)
                {
                    alsoResults[ordinal] = extraText;
                    stats.AlsoTracks++;
                    stats.AlsoBlocks += extraCues.Count;
                }
            }

            // The shared plan describes what the phase would read if nothing were in hand, and it is served out
            // of the primary track's fetch: netting it against what is in hand says "1 read" where the phase
            // makes nine window-sized ones, and comparing it raw says the phase missed a prediction it met by
            // construction. Both numbers are logged instead, and the miss flag is not armed - the fetches this
            // phase lives on were compared by the phase that made them, so counting them again would warn on
            // every file with two or more subtitle tracks.
            var (sharedLine, _) = policy.Compare(sharedPlan, reader.BytesRead - sharedBytesBefore, reader.ReadCalls - sharedCallsBefore);
            stats.PlanLines.Add(sharedLine);
            stats.PlanBound += sharedPlan.Coarse ? 1 : 0;
            stats.PlanExpectedBytes += sharedPlan.ExpectedBytes;
            stats.PlanExpectedCalls += sharedPlan.ExpectedCalls;
            sharedLine += string.Format(
                CultureInfo.InvariantCulture,
                " (served from this pass's fetch: {0} read(s) of its own)",
                reader.BytesRead - sharedBytesBefore);
            PluginLog.Info(sharedLine);
        }

        readWatch.Stop();
        stats.ReadMs = readWatch.Elapsed.TotalMilliseconds;

        // What the pass read, fetched and re-read, from the ledger rather than from the counters: a fetched
        // range no read used, or a byte that went to the file twice, is waste the pass paid for, and the
        // plan said it would not.
        stats.PrefetchedRanges = reader.Ledger.FetchedRanges;
        stats.PrefetchedBytes = reader.Ledger.FetchedBytes;
        stats.PrefetchedUnusedRanges = reader.Ledger.UnusedFetchedRanges;
        stats.PrefetchedUnusedBytes = reader.Ledger.UnusedFetchedBytes;
        stats.BytesReadTwice = reader.Ledger.BytesReadTwice;
        stats.BytesReadAfterFetch = reader.Ledger.BytesReadAfterFetch;
        stats.BytesFetchedOverDisk = reader.Ledger.BytesFetchedOverDisk;
        stats.MemoryServedReads = reader.MemoryServedReads;
        stats.WalkWindow = policy.CurrentWindow;
        stats.StorageProbeMs = policy.MsPerCall;
        stats.MeasuredMsPerRead = policy.MsPerCall;
        stats.MeasuredMbPerSecond = policy.BytesPerMs / 1000.0;

        // For every route, not just the walk: whether the numbers a decision rested on are measurements or
        // still the class defaults is the one thing this line exists to say, and a cue-indexed pass never
        // said it.
        PluginLog.Info("extract profile: " + policy.DescribeProfile());
        Diag("read ledger: " + reader.Ledger.Describe());
        if (reader.Ledger.BytesReadAfterFetch > 0)
        {
            PluginLog.Warn(
                $"extract: {label} read {reader.Ledger.BytesReadAfterFetch / 1e6:0.00} MB past the fetch "
                + "(a fetched range that did not cover the read it was made for) - " + reader.Ledger.Describe());
        }

        if (cues.Count == 0)
        {
            Diag("no subtitle blocks found");
            reason = "no subtitle blocks found";
            return false;
        }

        cues.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
        srtText = ToSrt(cues);
        stats.SubtitleBlocks = cues.Count;
        return srtText.Length > 0;
    }

    /// <summary>
    /// Bytes and read calls as the kernel sees them (<c>/proc/self/io</c>), when readable.
    ///
    /// The reader's own counters describe what it asked for; these describe what happened, which
    /// is what any claim about cost has to rest on.
    /// </summary>
    /// <returns>Bytes read and read syscalls, or (-1, -1) when unavailable.</returns>
    public static (long Bytes, long Calls) KernelIo()
    {
        if (!OperatingSystem.IsLinux())
        {
            return (-1, -1);
        }

        try
        {
            long bytes = -1;
            long calls = -1;
            foreach (var line in File.ReadLines("/proc/self/io"))
            {
                if (line.StartsWith("rchar:", StringComparison.Ordinal))
                {
                    bytes = long.Parse(line.AsSpan(6).Trim(), CultureInfo.InvariantCulture);
                }
                else if (line.StartsWith("syscr:", StringComparison.Ordinal))
                {
                    calls = long.Parse(line.AsSpan(6).Trim(), CultureInfo.InvariantCulture);
                }
            }

            return (bytes, calls);
        }
        catch
        {
            return (-1, -1);
        }
    }

    /// <summary>
    /// Fills the derived cost figures (latency, block rate, kernel totals) on a finished
    /// extraction, so the summary line states numbers rather than "unknown".
    /// </summary>
    /// <param name="stats">Stats to complete.</param>
    private static void FinaliseStats(MkvExtractionStats stats)
    {
        stats.ReadLatencyMs = stats.ReadCalls <= 0 ? 0 : stats.ReadMs / stats.ReadCalls;
        stats.BlocksPerSecond = stats.TotalMs <= 0
            ? 0
            : stats.SubtitleBlocks / (stats.TotalMs / 1000.0);

        var (bytes, calls) = KernelIo();
        if (bytes >= 0 && _kernelBytesAtStart >= 0)
        {
            stats.KernelBytesRead = bytes - _kernelBytesAtStart;
            stats.KernelReadCalls = calls - _kernelCallsAtStart;
        }
    }

    /// <summary>
    /// Marks the start of a measured extraction, so kernel counters can be reported as a delta.
    /// </summary>
    private static void MarkIoBaseline()
    {
        var (bytes, calls) = KernelIo();
        _kernelBytesAtStart = bytes;
        _kernelCallsAtStart = calls;
    }

    /// <summary>
    /// Reads the single block a cue point refers to, jumping over the rest of the cluster with
    /// CueRelativePosition. Returns false when that position does not hold a usable block, so
    /// the caller falls back to walking the cluster.
    /// </summary>
    /// <param name="reader">File reader.</param>
    /// <param name="clusterPosition">Position of the Cluster element's id.</param>
    /// <param name="clusterBound">
    /// Where to look for this cluster's end when the file does not state its size: the next cluster the
    /// index mentions, or the end of the file.
    /// </param>
    /// <param name="cueRef">Cue point.</param>
    /// <param name="track">Track being extracted.</param>
    /// <param name="cues">Collected subtitles.</param>
    /// <param name="stats">Cost counters.</param>
    /// <returns>True when the block was read.</returns>
    private static bool ReadIndexedBlock(
        BlobReader reader,
        long clusterPosition,
        long clusterBound,
        CueRef cueRef,
        SubtitleTrack track,
        List<Cue> cues,
        MkvExtractionStats stats)
    {
        if (cueRef.RelativePosition < 0
            || !TryReadClusterHeader(reader, clusterPosition, clusterBound, out var clusterDataStart, out var clusterEnd, out var exactEnd))
        {
            return false;
        }

        // The block's own time is its cluster's timecode plus the block's relative timecode.
        // CueTime is not used as the base: it is the seek point's timestamp, which for the
        // referenced block is already its own time, so adding the relative value on top
        // double-counts it (measured against ffmpeg: +51 ms, +459 ms).
        if (!TryReadClusterTimecode(reader, clusterDataStart, clusterEnd, out var clusterTimecode))
        {
            return false;
        }

        var before = cues.Count;
        if (!ReadBlockAtRelative(reader, clusterDataStart, clusterEnd, cueRef.RelativePosition, clusterTimecode, track, cues))
        {
            return false;
        }

        if (!exactEnd)
        {
            // The file states no size for this cluster, so clusterEnd is the next cluster the index
            // mentions rather than this one's own end: the located offset is contained by nothing the
            // file says. A block read outside its own cluster would carry a later cluster's timecode and,
            // with it, another subtitle's text, so the read only stands when the block it produced is the
            // cue point's own. Refusing here is safe: the caller walks the cluster, which is what every
            // unknown-size cluster did before this - only once per cluster now, not once per cue point.
            if (cues.Count == before || Math.Abs(cues[before].StartMs - cueRef.CueTimeMs) > 1)
            {
                if (cues.Count > before)
                {
                    cues.RemoveRange(before, cues.Count - before);
                }

                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads a cluster's header: where its body starts and where it ends.
    /// </summary>
    /// <param name="reader">File reader.</param>
    /// <param name="clusterPosition">Position of the Cluster element's id.</param>
    /// <param name="clusterDataStart">First byte of the cluster's body.</param>
    /// <param name="clusterEnd">One past the cluster's last byte.</param>
    /// <param name="upperBound">
    /// Where to look for the end of a cluster whose size the file does not state: the next cluster the
    /// index mentions, or the end of the file. Only used for those.
    /// </param>
    /// <param name="exactEnd">True when the cluster's own size gave the end.</param>
    /// <returns>True when a Cluster element is at that position.</returns>
    private static bool TryReadClusterHeader(
        BlobReader reader,
        long clusterPosition,
        long upperBound,
        out long clusterDataStart,
        out long clusterEnd,
        out bool exactEnd)
    {
        clusterDataStart = 0;
        clusterEnd = 0;
        exactEnd = false;
        if (!reader.TryReadElementHeaderAt(clusterPosition, out var id, out var size, out var headerLength)
            || id != IdCluster)
        {
            return false;
        }

        clusterDataStart = clusterPosition + headerLength;
        if (size == ulong.MaxValue)
        {
            // A streamed or non-seekable remux writes clusters with the unknown-size marker, and refusing
            // them here was expensive out of all proportion: every located read failed, so the cue-indexed
            // loop walked each cue's cluster instead - one located block became ~50 block headers and
            // ~200 KB of reads (33 525 reads / 133 MB against a plan of 1 319 / 1.57 MB, S26). The walk
            // below in ReadCluster has always accepted these clusters the same way (it ends them at the
            // file's end); the located path is stricter about it because it jumps to an offset the file
            // does not corroborate - which ReadIndexedBlock checks against the cue point's own time.
            clusterEnd = Math.Min(upperBound, reader.Length);
            return clusterEnd > clusterDataStart;
        }

        clusterEnd = clusterDataStart + (long)size;
        exactEnd = true;
        return true;
    }

    /// <summary>
    /// Reads the block at a position relative to the start of a cluster's body, following the
    /// SimpleBlock/BlockGroup shapes. Used both by the per-cue path and by the multi-track pass,
    /// which reads one cluster header and then every wanted track's block inside it.
    /// </summary>
    /// <param name="reader">File reader.</param>
    /// <param name="clusterDataStart">First byte of the cluster's body.</param>
    /// <param name="clusterEnd">One past the cluster's last byte.</param>
    /// <param name="relativePosition">Block position relative to the cluster's body.</param>
    /// <param name="clusterTimecode">The cluster's timecode.</param>
    /// <param name="track">Track being extracted.</param>
    /// <param name="cues">Collected subtitles.</param>
    /// <returns>True when the block was read.</returns>
    private static bool ReadBlockAtRelative(
        BlobReader reader,
        long clusterDataStart,
        long clusterEnd,
        long relativePosition,
        long clusterTimecode,
        SubtitleTrack track,
        List<Cue> cues)
    {
        var blockPosition = clusterDataStart + relativePosition;
        if (blockPosition >= clusterEnd || blockPosition >= reader.Length)
        {
            return false;
        }

        if (!reader.TryReadElementHeaderAt(blockPosition, out var blockId, out var blockSize, out var blockHeader)
            || blockSize == ulong.MaxValue)
        {
            return false;
        }

        var dataStart = blockPosition + blockHeader;
        var dataEnd = dataStart + (long)blockSize;

        if (blockId == IdSimpleBlock)
        {
            return ReadBlock(reader, dataStart, dataEnd, clusterTimecode, track, cues, false, 0);
        }

        if (blockId != IdBlockGroup)
        {
            return false;
        }

        long duration = 0;
        var blockStart = -1L;
        var blockEnd = -1L;
        var cursor = dataStart;
        while (cursor < dataEnd)
        {
            if (!reader.TryReadElementHeaderAt(cursor, out var childId, out var childSize, out var childHeader)
                || childSize == ulong.MaxValue)
            {
                break;
            }

            var childData = cursor + childHeader;
            if (childId == IdBlock)
            {
                blockStart = childData;
                blockEnd = childData + (long)childSize;
            }
            else if (childId == IdBlockDuration)
            {
                duration = (long)reader.ReadUnsigned(childData, (long)childSize);
            }

            cursor = childData + (long)childSize;
        }

        return blockStart >= 0
            && blockEnd > blockStart
            && ReadBlock(reader, blockStart, blockEnd, clusterTimecode, track, cues, true, duration);
    }

    /// <summary>
    /// Reads the Timecode element that opens a cluster (some muxers put a CRC-32 in front of
    /// it, so a couple of children are inspected).
    /// </summary>
    /// <param name="reader">File reader.</param>
    /// <param name="clusterDataStart">First byte of the cluster's body.</param>
    /// <param name="clusterEnd">One past the cluster's last byte.</param>
    /// <param name="timecode">The cluster's timecode in milliseconds.</param>
    /// <returns>True when it was found.</returns>
    private static bool TryReadClusterTimecode(
        BlobReader reader,
        long clusterDataStart,
        long clusterEnd,
        out long timecode)
    {
        timecode = 0;
        var cursor = clusterDataStart;
        for (var inspected = 0; inspected < 4 && cursor < clusterEnd; inspected++)
        {
            if (!reader.TryReadElementHeaderAt(cursor, out var id, out var size, out var headerLength)
                || size == ulong.MaxValue)
            {
                return false;
            }

            var dataStart = cursor + headerLength;
            if (id == IdClusterTimecode)
            {
                timecode = (long)reader.ReadUnsigned(dataStart, (long)size);
                return true;
            }

            if (id != IdCrc32 && id != IdVoid)
            {
                return false; // a block came first: this cluster has no leading timecode
            }

            cursor = dataStart + (long)size;
        }

        return false;
    }

    /// <summary>Reads a cluster: block headers first, payloads only for the wanted track.</summary>
    /// <param name="reader">File reader.</param>
    /// <param name="clusterPosition">Cluster header position.</param>
    /// <param name="track">Track being extracted.</param>
    /// <param name="cues">Collected subtitles.</param>
    /// <param name="stats">Cost counters.</param>
    /// <param name="policy">Read policy, which prices the window this cluster is read with.</param>
    /// <param name="wideWindow">
    /// True when the cue index named this cluster, so its blocks are worth reading in wide
    /// windows; false while walking every cluster, where a small window keeps the bytes down.
    /// </param>
    private static bool ReadCluster(
        BlobReader reader,
        long clusterPosition,
        SubtitleTrack track,
        List<Cue> cues,
        MkvExtractionStats stats,
        ReadPolicy policy,
        bool wideWindow = false)
    {
        if (!reader.TryReadElementHeaderAt(clusterPosition, out var id, out var size, out var headerLength) || id != IdCluster)
        {
            return false;
        }

        if (wideWindow && size != ulong.MaxValue)
        {
            // A cue cluster holds the subtitle block plus every audio frame of that stretch (~150 blocks
            // in practice), and the index did not say where the block is, so the cluster has to be read.
            // The window is priced from the storage: reading 11% of the cluster at one read per block is
            // free on an SSD and ruinous on a share that charges per round trip, so both are priced and
            // the cheaper wins. Never a fixed 4 MB: that moved 3.2 GB to collect ~50 KB of text once.
            reader.SetWindow(policy.ClusterWindow((long)size));
        }

        // The cluster is not counted here: every caller either counted it already (a cue point counting the
        // cluster it names before walking it) or counts the clusters it iterates itself. Counting it here as
        // well made a walked cluster read as two visits, which is why the mixed-index fixtures reported 15
        // visits for 10 cue points and the D17 mixed index 1205 for 803 - both double the reference.
        var dataStart = clusterPosition + headerLength;
        var dataEnd = size == ulong.MaxValue ? reader.Length : dataStart + (long)size;
        return ReadClusterChildren(reader, dataStart, Math.Min(dataEnd, reader.Length), track, cues, stats);
    }

    /// <summary>
    /// Walks the children of one cluster. Only this track's block payloads are read; every
    /// other block is skipped by advancing the cursor, which is what keeps a remux cheap.
    /// </summary>
    private static bool ReadClusterChildren(
        BlobReader reader,
        long dataStart,
        long dataEnd,
        SubtitleTrack track,
        List<Cue> cues,
        MkvExtractionStats stats)
    {
        var cursor = dataStart;
        long clusterTimecode = 0;

        while (cursor < dataEnd)
        {
            if (!reader.TryReadElementHeaderAt(cursor, out var childId, out var childSize, out var childLength))
            {
                return true; // end of readable data in this cluster
            }

            if (cursor > reader.Length - 64)
            {
                Diag($"near EOF: cursor={cursor} file={reader.Length} clusterEnd={dataEnd} childId=0x{childId:X} childSize={childSize} childLen={childLength}");
            }

            if (childId == IdCluster)
            {
                return true; // next cluster began: this one is done
            }

            var childData = cursor + childLength;
            if (childSize == ulong.MaxValue)
            {
                return true;
            }

            var childEnd = childData + (long)childSize;

            if (childId == IdClusterTimecode)
            {
                clusterTimecode = (long)reader.ReadUnsigned(childData, (long)childSize);
            }
            else if (childId == IdSimpleBlock)
            {
                if (!ReadBlock(reader, childData, childEnd, clusterTimecode, track, cues, false, 0))
                {
                    return false;
                }
            }
            else if (childId == IdBlockGroup)
            {
                long duration = 0;
                var blockStart = -1L;
                var blockEnd = -1L;
                var groupCursor = childData;
                while (groupCursor < childEnd)
                {
                    if (!reader.TryReadElementHeaderAt(groupCursor, out var groupId, out var groupSize, out var groupLength))
                    {
                        break;
                    }

                    if (groupSize == ulong.MaxValue)
                    {
                        break;
                    }

                    var groupData = groupCursor + groupLength;
                    if (groupId == IdBlock)
                    {
                        blockStart = groupData;
                        blockEnd = groupData + (long)groupSize;
                    }
                    else if (groupId == IdBlockDuration)
                    {
                        duration = (long)reader.ReadUnsigned(groupData, (long)groupSize);
                    }

                    groupCursor = blockEnd > groupCursor && groupId == IdBlock
                        ? blockEnd
                        : groupData + (long)groupSize;
                }

                if (blockStart >= 0 && blockEnd > blockStart
                    && !ReadBlock(reader, blockStart, blockEnd, clusterTimecode, track, cues, true, duration))
                {
                    return false;
                }
            }

            if (childEnd > dataEnd)
            {
                // A declared size that runs past the cluster is a malformed (or truncated)
                // element; stop here rather than walking into the next cluster's bytes.
                Diag($"child 0x{childId:X} at {cursor} (size {childSize}) overruns cluster end {dataEnd}");
                return true;
            }

            cursor = childEnd;
        }

        return true;
    }

    /// <summary>
    /// Metadata-only walk over every cluster: headers and block headers are read, payloads
    /// are skipped. Used when the cue index says nothing about the wanted track.
    /// </summary>
    /// <param name="reader">File reader.</param>
    /// <param name="startPosition">Where the walk begins.</param>
    /// <param name="endPosition">Where it must stop.</param>
    /// <param name="track">Track being extracted.</param>
    /// <param name="cues">Collected subtitles.</param>
    /// <param name="stats">Cost counters.</param>
    /// <param name="progress">Progress callback, or null.</param>
    /// <param name="policy">Read policy, which prices the window the walk reads with.</param>
    /// <param name="kernelCallsAtStart">Kernel read calls when the pass started, for the live progress line.</param>
    /// <param name="cancellationToken">Cancels the walk.</param>
    /// <returns>True when the walk completed.</returns>
    private static bool ScanClusters(
        BlobReader reader,
        long startPosition,
        long endPosition,
        SubtitleTrack track,
        List<Cue> cues,
        MkvExtractionStats stats,
        Action<string>? progress,
        ReadPolicy policy,
        long kernelCallsAtStart,
        CancellationToken cancellationToken = default)
    {
        var cursor = startPosition;
        var visited = 0;
        endPosition = Math.Min(endPosition, reader.Length);

        // The window is not decided by a probe taken before the walk, and not left alone either: a wrong
        // "reads are cheap" verdict costs thousands of reads at 16-33 ms each, which a user experiences as
        // "it takes minutes to start, then it is fast". The walk measures its own reads and asks the policy
        // what the next stretch should cost, re-pricing through the file and saying so in the log.
        var walkWatch = Stopwatch.StartNew();
        var readsAtStart = reader.ReadCalls;
        var bytesAtStart = reader.BytesRead;
        var lastPriceAt = 0;

        while (cursor < endPosition)
        {
            if (!reader.TryReadElementHeaderAt(cursor, out var id, out var size, out var headerLength))
            {
                break;
            }

            var dataStart = cursor + headerLength;

            if (id == IdCluster)
            {
                visited++;
                stats.ClustersVisited++;

                // Per cluster, not every 64th (B7): the button the user pressed has to stop the pass at the
                // next cluster boundary, and a boundary is a place where nothing is half-read. The old
                // sampling meant up to 64 clusters - minutes on the user's share - of a walk nobody wanted
                // any more. The caller turns this into a cancellation.
                if (cancellationToken.IsCancellationRequested)
                {
                    return false;
                }
                if (progress is not null && visited % ProgressEveryClusters == 0)
                {
                    // The scan is the one route that reads most of the file, so this is the line someone
                    // spends minutes watching - and it read "0,0 MB" for the whole pass, because it printed
                    // the stats field that only the cue-indexed loop fills. The numbers come from the reader
                    // the pass is reading through (through the kernel counters where the platform provides
                    // them, the reader's own count otherwise), which is what the other routes' live lines
                    // report, and the pass's own counters are brought up to date with it so the summary at
                    // the end carries the same figures the live lines showed.
                    stats.BytesRead = reader.BytesRead;
                    stats.ReadCalls = reader.ReadCalls;
                    var (kernelBytesNow, kernelCallsNow) = KernelIo();
                    var liveBytes = kernelBytesNow >= 0 && _kernelBytesAtStart >= 0
                        ? kernelBytesNow - _kernelBytesAtStart
                        : reader.BytesRead;
                    if (kernelBytesNow >= 0 && kernelCallsNow >= 0 && kernelCallsAtStart >= 0)
                    {
                        stats.KernelBytesRead = kernelBytesNow - _kernelBytesAtStart;
                        stats.KernelReadCalls = kernelCallsNow - _kernelCallsAtStart;
                    }

                    progress($"scanning clusters ({visited} read, {liveBytes / 1e6:0.0} MB, {cues.Count} subtitles found)");
                }

                if (visited - lastPriceAt >= ProgressEveryClusters)
                {
                    lastPriceAt = visited;
                    var reads = reader.ReadCalls - readsAtStart;
                    var bytes = reader.BytesRead - bytesAtStart;
                    var covered = Math.Max(1, cursor - startPosition);
                    if (reads >= 32)
                    {
                        var clustersPerByte = (double)visited / covered;
                        var remaining = (long)Math.Max(1, (endPosition - cursor) * clustersPerByte);
                        var priced = policy.WalkWindow(
                            endPosition - cursor,
                            (double)bytes / Math.Max(1, visited),
                            (double)reads / Math.Max(1, visited),
                            remaining);
                        var was = reader.WindowSize;
                        var now = reader.SetWindow(priced);
                        if (now != was)
                        {
                            PluginLog.Info(
                                $"extract: walk window {was / 1024} KB -> {now / 1024} KB after {visited} cluster(s) "
                                + $"({reads} reads carrying {bytes / 1e6:0.00} MB, {walkWatch.Elapsed.TotalMilliseconds / reads:0.00} ms each) "
                                + $"- re-priced from the walk's own reads, not from a probe before it");
                        }
                    }
                }

                var dataEnd = size == ulong.MaxValue ? endPosition : Math.Min(dataStart + (long)size, endPosition);
                if (!ReadClusterChildren(reader, dataStart, dataEnd, track, cues, stats))
                {
                    return false;
                }

                cursor = dataEnd;
                continue;
            }

            if (size == ulong.MaxValue)
            {
                cursor = dataStart; // streamed element: step inside
                continue;
            }

            cursor = dataStart + (long)size;

            // Clusters with unknown size are what a streamed ingest produces; stop scanning
            // rather than guess.
            if (id == IdVoid || id == IdCrc32)
            {
                continue;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads one block. The block header is read first (a dozen bytes) and the payload only
    /// when the block belongs to the wanted track — reading it for every block is what makes
    /// a naive parser read the whole video.
    /// </summary>
    private static bool ReadBlock(
        BlobReader reader,
        long dataStart,
        long dataEnd,
        long clusterTimecode,
        SubtitleTrack track,
        List<Cue> cues,
        bool hasDuration,
        long durationTicks)
    {
        var length = dataEnd - dataStart;
        if (length <= 4)
        {
            return true; // empty block
        }

        Span<byte> header = stackalloc byte[16];
        var headerBytes = (int)Math.Min(header.Length, Math.Min(length, reader.Length - dataStart));
        if (headerBytes <= 0 || reader.ReadNear(dataStart, header[..headerBytes]) < headerBytes)
        {
            return true; // truncated tail: nothing more to read here
        }

        var offset = 0;
        if (!TryReadVint(header[..headerBytes], ref offset, out var trackNumber, out _))
        {
            return false;
        }

        if (trackNumber != track.TrackNumber)
        {
            return true; // another track: the payload is never touched
        }

        // The block belongs to the track we are after, whether or not its text turns out to be
        // blank: this is what the cue-parity check counts (see SubtitleTrack.BlocksFound).
        track.BlocksFound++;

        if (offset + 3 > headerBytes)
        {
            return false;
        }

        var relative = BinaryPrimitives.ReadInt16BigEndian(header[offset..]);
        var flags = header[offset + 2];
        offset += 3;

        var lacing = (flags >> 1) & 0x03;
        if (lacing != 0)
        {
            // Laced text subtitle blocks are rare; decoding one wrongly would be silent
            // corruption, so hand the file back to ffmpeg instead.
            return false;
        }

        var payloadLength = (int)(length - offset);
        if (payloadLength <= 0)
        {
            return true;
        }

        var payload = new byte[payloadLength];
        var payloadRead = reader.ReadNear(dataStart + offset, payload);
        if (payloadRead < payloadLength)
        {
            payload = payload.AsSpan(0, payloadRead).ToArray();
        }

        var text = Encoding.UTF8.GetString(payload);
        if (track.MarkEmitted(dataStart))
        {
            AddCue(cues, clusterTimecode, relative, text, hasDuration, durationTicks, track);
        }

        return true;
    }

    private static void AddCue(
        List<Cue> cues,
        long clusterTimecode,
        short relative,
        string rawText,
        bool hasDuration,
        long durationTicks,
        SubtitleTrack track)
    {
        var text = NormalizeText(rawText, track);
        if (string.IsNullOrWhiteSpace(text))
        {
            return;
        }

        var start = clusterTimecode + relative;
        var given = hasDuration && durationTicks > 0;
        var end = given ? start + durationTicks : start + 2000;
        cues.Add(new Cue(start, end, text, durationGuessed: !given));
    }

    /// <summary>Reads a SeekHead payload: which elements it points at, and where.</summary>
    private static List<(ulong ElementId, long Position)> ParseSeekHead(byte[] payload)
    {
        var entries = new List<(ulong, long)>();
        var offset = 0;
        while (offset < payload.Length)
        {
            if (!TryReadVint(payload, ref offset, out var id, out var unknown, keepMarker: true) || unknown)
            {
                break;
            }

            if (!TryReadVint(payload, ref offset, out var size, out var sizeUnknown) || sizeUnknown)
            {
                break;
            }

            var dataStart = offset;
            var dataEnd = (int)(dataStart + (long)size);
            if (dataEnd > payload.Length)
            {
                break;
            }

            if (id == IdSeek)
            {
                ulong seekId = 0;
                long seekPosition = -1;
                var cursor = dataStart;
                while (cursor < dataEnd)
                {
                    if (!TryReadVint(payload, ref cursor, out var childId, out _, keepMarker: true)
                        || !TryReadVint(payload, ref cursor, out var childSize, out _))
                    {
                        break;
                    }

                    var childEnd = (int)(cursor + (long)childSize);
                    if (childEnd > payload.Length)
                    {
                        break;
                    }

                    if (childId == IdSeekId)
                    {
                        seekId = ReadUnsignedBytes(payload.AsSpan(cursor, (int)childSize));
                    }
                    else if (childId == IdSeekPosition)
                    {
                        seekPosition = (long)ReadUnsignedBytes(payload.AsSpan(cursor, (int)childSize));
                    }

                    cursor = childEnd;
                }

                if (seekId != 0 && seekPosition >= 0)
                {
                    entries.Add((seekId, seekPosition));
                }
            }

            offset = dataEnd;
        }

        return entries;
    }

    /// <summary>Reads track entries (number, type, codec) out of a Tracks payload.</summary>
    private static List<SubtitleTrack> ParseTrackEntries(byte[] payload)
    {
        var tracks = new List<SubtitleTrack>();
        var offset = 0;
        while (offset < payload.Length)
        {
            if (!TryReadVint(payload, ref offset, out var id, out _, keepMarker: true)
                || !TryReadVint(payload, ref offset, out var size, out var unknown)
                || unknown)
            {
                break;
            }

            var dataStart = offset;
            var dataEnd = (int)(dataStart + (long)size);
            if (dataEnd > payload.Length)
            {
                break;
            }

            if (id == IdTrackEntry)
            {
                var track = new SubtitleTrack();
                var cursor = dataStart;
                while (cursor < dataEnd)
                {
                    if (!TryReadVint(payload, ref cursor, out var childId, out _, keepMarker: true)
                        || !TryReadVint(payload, ref cursor, out var childSize, out var childUnknown)
                        || childUnknown)
                    {
                        break;
                    }

                    var childEnd = (int)(cursor + (long)childSize);
                    if (childEnd > payload.Length)
                    {
                        break;
                    }

                    switch (childId)
                    {
                        case IdTrackNumber:
                            track.TrackNumber = ReadUnsignedBytes(payload.AsSpan(cursor, (int)childSize));
                            break;
                        case IdTrackType:
                            track.TrackType = (int)ReadUnsignedBytes(payload.AsSpan(cursor, (int)childSize));
                            break;
                        case IdCodecId:
                            track.CodecId = Encoding.UTF8.GetString(payload, cursor, (int)childSize).Trim('\0', ' ');
                            break;
                        case IdCodecPrivate:
                            track.CodecPrivate = payload.AsSpan(cursor, (int)childSize).ToArray();
                            break;
                        case IdContentEncodings:
                            track.Compressed = true;
                            break;
                    }

                    cursor = childEnd;
                }

                Diag($"  track entry: number={track.TrackNumber} type={track.TrackType} codec='{track.CodecId}'");
                if (track.TrackNumber != 0)
                {
                    tracks.Add(track);
                }
            }

            offset = dataEnd;
        }

        return tracks;
    }

    /// <summary>
    /// Reads the cue points the Cues element holds for one track: cluster offset, the block's
    /// offset inside that cluster when the muxer recorded it, and the cue time. The whole index
    /// is already in memory, so this costs no further reads.
    /// </summary>
    /// <param name="payload">Cues element body.</param>
    /// <param name="trackNumber">Track whose cue points are wanted.</param>
    /// <param name="cuePoints">Total cue points seen, for the log.</param>
    /// <returns>One entry per cue point belonging to the track.</returns>
    private static List<CueRef> ParseCueRefs(byte[] payload, ulong trackNumber, out long cuePoints)
    {
        var refs = new List<CueRef>();
        cuePoints = 0;
        var offset = 0;

        while (offset < payload.Length)
        {
            if (!TryReadVint(payload, ref offset, out var id, out _, keepMarker: true)
                || !TryReadVint(payload, ref offset, out var size, out var unknown)
                || unknown)
            {
                break;
            }

            var dataStart = offset;
            var dataEnd = (int)(dataStart + (long)size);
            if (dataEnd > payload.Length)
            {
                break;
            }

            if (id == IdCuePoint)
            {
                cuePoints++;
                var cursor = dataStart;
                var matched = false;
                long clusterPosition = -1;
                long relativePosition = -1;
                long cueTime = 0;

                while (cursor < dataEnd)
                {
                    if (!TryReadVint(payload, ref cursor, out var childId, out _, keepMarker: true)
                        || !TryReadVint(payload, ref cursor, out var childSize, out var childUnknown)
                        || childUnknown)
                    {
                        break;
                    }

                    var childEnd = (int)(cursor + (long)childSize);
                    if (childEnd > payload.Length)
                    {
                        break;
                    }

                    if (childId == IdCueTime)
                    {
                        cueTime = (long)ReadUnsignedBytes(payload.AsSpan(cursor, (int)childSize));
                    }
                    else if (childId == IdCueTrackPositions)
                    {
                        var positionsCursor = cursor;
                        ulong cueTrack = 0;

                        // One cue point carries one CueTrackPositions *per track*: mkvmerge writes the
                        // video's and every subtitle track's position inside the same cue point, with the
                        // highest track number last. The cluster and block offsets belong to the track of
                        // the CueTrackPositions they sit in, so they are read into locals and only adopted
                        // when that track is the one being extracted. Adopting the last position instead
                        // (which is what this did) hands a subtitle track the video's or a neighbouring
                        // subtitle track's offsets: on kopps 104 of 829 cue points then named a block that
                        // belongs to another track, and on Sune i Grekland all 1019 did, so every one of
                        // them had to be found by walking its cluster instead.
                        var positionCluster = -1L;
                        var positionRelative = -1L;
                        while (positionsCursor < childEnd)
                        {
                            if (!TryReadVint(payload, ref positionsCursor, out var posId, out _, keepMarker: true)
                                || !TryReadVint(payload, ref positionsCursor, out var posSize, out var posUnknown)
                                || posUnknown)
                            {
                                break;
                            }

                            var posEnd = (int)(positionsCursor + (long)posSize);
                            if (posEnd > payload.Length)
                            {
                                break;
                            }

                            if (posId == IdCueTrack)
                            {
                                cueTrack = ReadUnsignedBytes(payload.AsSpan(positionsCursor, (int)posSize));
                            }
                            else if (posId == IdCueClusterPosition)
                            {
                                positionCluster = (long)ReadUnsignedBytes(payload.AsSpan(positionsCursor, (int)posSize));
                            }
                            else if (posId == IdCueRelativePosition)
                            {
                                positionRelative = (long)ReadUnsignedBytes(payload.AsSpan(positionsCursor, (int)posSize));
                            }

                            positionsCursor = posEnd;
                        }

                        if (cueTrack == trackNumber)
                        {
                            matched = true;
                            clusterPosition = positionCluster;
                            relativePosition = positionRelative;
                        }
                    }

                    cursor = childEnd;
                }

                if (matched && clusterPosition >= 0)
                {
                    refs.Add(new CueRef(clusterPosition, relativePosition, cueTime));
                }
            }

            offset = dataEnd;
        }

        return refs;
    }

    /// <summary>
    /// Turns an element header position into its payload position and size, checking the id.
    /// SeekHead positions point at headers, not payloads — reading a payload from a header
    /// offset is how an index lookup silently lands in the wrong bytes.
    /// </summary>
    /// <param name="reader">File reader.</param>
    /// <param name="headerPosition">Position of the element id.</param>
    /// <param name="expectedId">Element id we expect there.</param>
    /// <param name="dataPosition">Payload position.</param>
    /// <param name="size">Payload size.</param>
    /// <returns>True when the header matched and the size is known.</returns>
    private static bool TryResolveElement(
        BlobReader reader,
        long headerPosition,
        ulong expectedId,
        out long dataPosition,
        out long size)
    {
        dataPosition = -1;
        size = 0;
        if (!reader.TryReadElementHeaderAt(headerPosition, out var id, out var elementSize, out var headerLength)
            || id != expectedId
            || elementSize == ulong.MaxValue)
        {
            return false;
        }

        dataPosition = headerPosition + headerLength;
        size = (long)elementSize;
        return true;
    }

    /// <summary>
    /// Turns one subtitle block into plain text. Matroska's S_TEXT/ASS blocks hold the ASS
    /// payload (layer, style, margins… then the text) rather than a "Dialogue:" line, and
    /// Comment blocks carry no viewable subtitle. WebVTT blocks hold the cue text with the
    /// timing in the block header, like S_TEXT/UTF8, but can carry WebVTT markup.
    /// </summary>
    private static string NormalizeText(string raw, SubtitleTrack track)
    {
        if (track.IsWebVtt)
        {
            return CleanWebVttText(raw);
        }

        var text = raw.Replace("\r\n", "\n").Trim('\n', '\r', ' ', '\t');
        if (text.Length == 0)
        {
            return string.Empty;
        }

        if (text.StartsWith("Comment:", StringComparison.OrdinalIgnoreCase))
        {
            return string.Empty;
        }

        if (text.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase))
        {
            var colon = text.IndexOf(':');
            text = SkipAssFields(text[(colon + 1)..], 9);
        }
        else if (track.IsAss)
        {
            // Matroska ASS payload: ReadOrder,Layer,Style,Name,MarginL,MarginR,MarginV,
            // Effect then the text — one field fewer than a "Dialogue:" line.
            text = SkipAssFields(text, 8);
        }

        var builder = new StringBuilder(text.Length);
        var inTag = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '{')
            {
                inTag = true;
                continue;
            }

            if (c == '}')
            {
                inTag = false;
                continue;
            }

            if (inTag)
            {
                continue;
            }

            if (c == '\\' && i + 1 < text.Length && (text[i + 1] == 'N' || text[i + 1] == 'n'))
            {
                builder.Append('\n');
                i++;
                continue;
            }

            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    /// <summary>
    /// Whether a Matroska subtitle codec ID holds text that can be read through the container index.
    ///
    /// The WebVTT IDs are the important part of this list. A muxer picks the ID - ffmpeg writes
    /// <c>D_WEBVTT/SUBTITLES</c>, mkvmerge writes <c>S_TEXT/WEBVTT</c> (both measured) - and when they
    /// were missing, every WebVTT track was rejected by the index reader and demuxed by ffmpeg instead:
    /// a whole-file read per subtitle, which on a NAS-hosted episode is minutes rather than the
    /// milliseconds an indexed read costs. WebVTT in Matroska is stored text-per-block exactly like
    /// S_TEXT/UTF8 (the timing is in the block header, not the payload), so it needs no ffmpeg.
    /// </summary>
    /// <param name="codecId">Matroska codec ID (e.g. "S_TEXT/UTF8").</param>
    /// <returns>True when the codec is text.</returns>
    public static bool IsTextSubtitleCodecId(string codecId)
        => codecId is "S_TEXT/UTF8" or "S_TEXT/ASS" or "S_TEXT/SSA" or "S_SSA/ASS"
            or "S_TEXT/WEBVTT" or "D_WEBVTT/SUBTITLES" or "D_WEBVTT/CAPTIONS";

    /// <summary>
    /// Whether a Matroska codec ID is WebVTT.
    /// </summary>
    /// <param name="codecId">Matroska codec ID.</param>
    /// <returns>True when the track holds WebVTT cues.</returns>
    public static bool IsWebVttCodecId(string codecId)
        => codecId is "S_TEXT/WEBVTT" or "D_WEBVTT/SUBTITLES" or "D_WEBVTT/CAPTIONS";

    /// <summary>
    /// Cleans one WebVTT cue payload for an SRT file.
    ///
    /// Matroska stores a WebVTT track with the same text-per-block layout as S_TEXT/UTF8: the timing
    /// lives in the block header, not the payload (checked against a muxed fixture, where each block's
    /// data is only the cue text). What the payload can still carry is WebVTT markup — voice spans
    /// (<c>&lt;v Name&gt;</c>), class spans, inline cue timestamps — and HTML entities, plus its own
    /// timing line if a muxer kept it. The block header stays the timing authority either way.
    /// </summary>
    /// <param name="raw">Cue payload read from the block.</param>
    /// <returns>The cue text without markup.</returns>
    public static string CleanWebVttText(string raw)
    {
        var text = (raw ?? string.Empty).Replace("\r\n", "\n").Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        // Any cue timing line inside the payload is dropped: the block header says when the cue plays,
        // and a muxer that kept the line would otherwise inject it into the SRT text.
        var lines = text.Split('\n');
        var kept = new List<string>(lines.Length);
        foreach (var line in lines)
        {
            if (line.Contains("-->", StringComparison.Ordinal))
            {
                continue;
            }

            kept.Add(line);
        }

        text = string.Join('\n', kept).Trim();
        if (text.Length == 0)
        {
            return string.Empty;
        }

        var builder = new StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];

            if (c == '<')
            {
                var close = text.IndexOf('>', i + 1);
                if (close > i)
                {
                    i = close;
                    continue;
                }
            }

            if (c == '&')
            {
                if (text.AsSpan(i).StartsWith("&amp;", StringComparison.Ordinal))
                {
                    builder.Append('&');
                    i += 4;
                    continue;
                }

                if (text.AsSpan(i).StartsWith("&lt;", StringComparison.Ordinal))
                {
                    builder.Append('<');
                    i += 3;
                    continue;
                }

                if (text.AsSpan(i).StartsWith("&gt;", StringComparison.Ordinal))
                {
                    builder.Append('>');
                    i += 3;
                    continue;
                }

                if (text.AsSpan(i).StartsWith("&nbsp;", StringComparison.Ordinal))
                {
                    builder.Append(' ');
                    i += 5;
                    continue;
                }
            }

            builder.Append(c);
        }

        return builder.ToString().Trim();
    }

    private static string SkipAssFields(string value, int fields)
    {
        var seen = 0;
        var index = 0;
        while (index < value.Length && seen < fields)
        {
            if (value[index] == ',')
            {
                seen++;
            }

            index++;
        }

        return seen == fields ? value[index..] : value;
    }

    /// <summary>
    /// Hands over every wanted track whose last cue this cluster held, so its job can start syncing
    /// while the rest of the file is still being read.
    /// </summary>
    /// <param name="lastClusterOf">Cluster position to the tracks that finish there.</param>
    /// <param name="clusterPosition">The cluster just processed.</param>
    /// <param name="wanted">Wanted tracks, in the order they were collected.</param>
    /// <param name="expectedCues">Cue points the index holds per wanted track, keyed by its position in <paramref name="wanted"/>.</param>
    /// <param name="published">Tracks already handed over.</param>
    /// <param name="publish">Callback into the caller, null when nobody is listening.</param>
    private static void PublishFinishedTracks(
        SortedDictionary<long, List<int>> lastClusterOf,
        long clusterPosition,
        List<(int Ordinal, SubtitleTrack Track, List<Cue> Cues)> wanted,
        Dictionary<int, int> expectedCues,
        HashSet<int> published,
        Action<int, string>? publish)
    {
        if (publish is null)
        {
            return;
        }

        foreach (var (finishAt, indices) in lastClusterOf)
        {
            if (finishAt > clusterPosition)
            {
                break;
            }

            foreach (var index in indices)
            {
                if (!published.Add(index))
                {
                    continue;
                }

                var done = wanted[index];
                if (done.Cues.Count == 0)
                {
                    continue;
                }

                // Hand a track over only when every cue point the index knows about produced a block.
                // A track that is one block short would otherwise be stored (and published to the
                // job that is waiting for it) as if it were complete.
                if (expectedCues.TryGetValue(index, out var expected) && done.Track.BlocksFound < expected)
                {
                    continue;
                }

                done.Cues.Sort((a, b) => a.StartMs.CompareTo(b.StartMs));
                var text = ToSrt(done.Cues);
                if (text.Length > 0)
                {
                    publish(done.Ordinal, text);
                }
            }
        }
    }

    private static string ToSrt(List<Cue> cues)
    {
        var builder = new StringBuilder();
        var index = 1;
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var end = cue.EndMs;

            // Only a guessed end may be pulled back to the next cue. A duration the file stated is
            // written as it is, whatever its length: inferring the guess from the number (2000 ms)
            // rewrote real two-second cues (F19).
            if (i + 1 < cues.Count && (end > cues[i + 1].StartMs || cue.DurationGuessed))
            {
                var next = cues[i + 1].StartMs;
                end = next > cue.StartMs ? Math.Min(next - 1, cue.StartMs + 7000) : cue.StartMs + 1500;
            }

            if (end <= cue.StartMs)
            {
                continue;
            }

            builder.Append(index.ToString(CultureInfo.InvariantCulture)).Append('\n');
            builder.Append(FormatTime(cue.StartMs)).Append(" --> ").Append(FormatTime(end)).Append('\n');
            builder.Append(cue.Text).Append("\n\n");
            index++;
        }

        return builder.ToString();
    }

    private static string FormatTime(long totalMs)
    {
        if (totalMs < 0)
        {
            totalMs = 0;
        }

        var ms = totalMs % 1000;
        var totalSeconds = totalMs / 1000;
        var seconds = totalSeconds % 60;
        var minutes = (totalSeconds / 60) % 60;
        var hours = totalSeconds / 3600;
        return string.Format(CultureInfo.InvariantCulture, "{0:00}:{1:00}:{2:00},{3:000}", hours, minutes, seconds, ms);
    }

    private static ulong ReadUnsignedBytes(ReadOnlySpan<byte> data)
    {
        ulong value = 0;
        foreach (var b in data)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    /// <summary>
    /// Reads one EBML VINT. Element ids keep their marker bit (it is part of the id); sizes
    /// drop it, and an all-ones payload marks an unknown size.
    /// </summary>
    private static bool TryReadVint(
        ReadOnlySpan<byte> buffer,
        ref int offset,
        out ulong value,
        out bool unknown,
        bool keepMarker = false)
    {
        value = 0;
        unknown = false;
        if (offset >= buffer.Length)
        {
            return false;
        }

        var first = buffer[offset];
        if (first == 0)
        {
            return false;
        }

        var length = 1;
        var mask = 0x80;
        while ((first & mask) == 0)
        {
            length++;
            mask >>= 1;
            if (length > 8)
            {
                return false;
            }
        }

        if (offset + length > buffer.Length)
        {
            return false;
        }

        ulong raw = 0;
        for (var i = 0; i < length; i++)
        {
            raw = (raw << 8) | buffer[offset + i];
        }

        var valueBits = (1UL << (7 * length)) - 1;
        unknown = (raw & valueBits) == valueBits;
        value = keepMarker ? raw : raw & valueBits;
        offset += length;
        return true;
    }

    private sealed class SubtitleTrack
    {
        public ulong TrackNumber { get; set; }

        public int TrackType { get; set; }

        public string CodecId { get; set; } = string.Empty;

        public byte[]? CodecPrivate { get; set; }

        public bool Compressed { get; set; }

        /// <summary>
        /// How many blocks of this track the current pass has actually read out of the container.
        ///
        /// This is the evidence the cue-parity check needs: a block that was found and whose text
        /// turned out to be blank still counts, a cue point whose block could not be located does
        /// not. Comparing the extracted cue count against the index's cue count cannot tell those
        /// two apart, which is why the check is made here instead.
        /// </summary>
        public int BlocksFound { get; set; }

        /// <summary>
        /// True the first time this pass reaches the block whose payload starts at that position.
        ///
        /// A block is one subtitle, however many routes reach it: the cue index names it when the cue
        /// point carries a block offset, and the walk over its cluster finds it whether or not the index
        /// located it. Where the index locates only some of a track's cue points - the mixed-index shape
        /// this project's fixtures and the D17 patch both produce - the walk of a cluster hands back a
        /// block the cue point next to it already emitted, and the same subtitle came out twice with two
        /// different ends (one from the block's own duration, one from the two-second default this
        /// extractor uses when a block carries none). A block's identity is where it sits, so that is what
        /// is remembered here, per pass.
        /// </summary>
        /// <param name="blockPosition">Start of the block's payload.</param>
        /// <returns>True when this pass had not yet reached that block.</returns>
        public bool MarkEmitted(long blockPosition) => _emitted.Add(blockPosition);

        private readonly HashSet<long> _emitted = new();

        public bool IsSubtitle => TrackType == 17;

        public bool IsText => IsTextSubtitleCodecId(CodecId);

        /// <summary>
        /// Whether this track is WebVTT. See <see cref="IsWebVttCodecId"/> for why the codec ID
        /// matters.
        /// </summary>
        public bool IsWebVtt => IsWebVttCodecId(CodecId);


        public bool IsAss => CodecId is "S_TEXT/ASS" or "S_SSA/ASS";
    }

    /// <summary>
    /// One cue point: where its cluster is, where the block sits inside that cluster (when the
    /// muxer recorded it) and the cue's timecode in milliseconds.
    /// </summary>
    /// <param name="ClusterOffset">Cluster element offset, relative to the segment data.</param>
    /// <param name="RelativePosition">Block offset inside the cluster, or -1 when absent.</param>
    /// <param name="CueTimeMs">Cue timecode in milliseconds.</param>
    private sealed record CueRef(long ClusterOffset, long RelativePosition, long CueTimeMs);

    private sealed class Cue
    {
        public Cue(long startMs, long endMs, string text, bool durationGuessed = false)
        {
            StartMs = startMs;
            EndMs = endMs;
            Text = text;
            DurationGuessed = durationGuessed;
        }

        public long StartMs { get; }

        public long EndMs { get; }

        public string Text { get; }

        /// <summary>
        /// True when the block carried no BlockDuration, so <see cref="EndMs"/> is this reader's own
        /// guess. Only a guessed end may be pulled back to the next cue: a stated duration that happens
        /// to be exactly 2000 ms used to be treated as a guess and rewritten (F19).
        /// </summary>
        public bool DurationGuessed { get; }
    }

    /// <summary>
    /// Positional reader that counts what it costs. Reads are unbuffered, so seeking never
    /// drags a buffer's worth of unrelated bytes along, and element headers can be read for
    /// the few bytes they occupy.
    /// </summary>
    private sealed class BlobReader
    {
        /// <summary>Largest window the reader may use.</summary>
        // Owned by the policy: a window is a decision about the storage, and there is one place that makes
        // those. Callers that want to read as few bytes as possible (the per-cue path, where the exact
        // block position is known) get a small window from the plan instead.
        public const int MaxWindowSize = ReadPolicy.MaxWindow;

        /// <summary>Gets or sets the policy that decided this pass's window and fetch.</summary>
        public ReadPolicy? Policy { get; set; }

        /// <summary>Gets the ledger of what this pass read from the file and what it fetched first.</summary>
        public ReadLedger Ledger { get; } = new();

        /// <summary>Gets how many reads were answered from memory rather than from the file.</summary>
        public int MemoryServedReads { get; private set; }

        /// <summary>Gets the plan the reader is following, when it has one.</summary>
        public ReadPlan? Plan { get; private set; }

        private readonly FileStream _stream;
        private readonly byte[] _window = new byte[MaxWindowSize];
        private long _windowStart = -1;
        private int _windowLength;

        /// <summary>
        /// Gets or sets how much is read in one go. Enumerating the blocks of a cluster the cue index
        /// pointed at wants this large (a cluster holds ~150 blocks, mostly audio frames, and each one
        /// otherwise costs its own round trip); walking cluster headers in a metadata scan wants it small.
        /// </summary>
        public int WindowSize { get; set; } = ReadPolicy.MinWindow * 8;

        /// <summary>
        /// Applies a plan: its window, and the reads it expects fetched in parallel before the pass needs
        /// them. Every range the plan asks for is registered in the ledger, and a range this pass has
        /// already read or fetched is never fetched twice.
        /// </summary>
        /// <param name="plan">The plan to follow.</param>
        /// <param name="cancellationToken">Cancels the fetch.</param>
        public void Apply(ReadPlan plan, CancellationToken cancellationToken = default)
        {
            Plan = plan;
            WindowSize = Math.Clamp(plan.WindowBytes, ReadPolicy.MinWindow, MaxWindowSize);
            if (Policy is not null)
            {
                Policy.CurrentWindow = WindowSize;
            }

            var fetches = new List<(long Start, int Length)>();
            long bytes = 0;
            foreach (var read in plan.Fetches)
            {
                if (Ledger.AlreadyInHand(read.Start, read.Length))
                {
                    continue;
                }

                fetches.Add((read.Start, read.Length));
                bytes += read.Length;
            }

            if (fetches.Count > 0)
            {
                Prefetch(fetches, ReadPolicy.PrefetchParallelism, cancellationToken);
            }
        }

        /// <summary>Sets the window the pass reads with, from a re-pricing rather than a plan.</summary>
        /// <param name="window">Requested window in bytes.</param>
        /// <returns>The window actually in force.</returns>
        public int SetWindow(int window)
        {
            WindowSize = Math.Clamp(window, ReadPolicy.MinWindow, MaxWindowSize);
            if (Policy is not null)
            {
                Policy.CurrentWindow = WindowSize;
            }

            return WindowSize;
        }

        public BlobReader(FileStream stream, string path = "")
        {
            _stream = stream;
            Length = stream.Length;
            Path = path;
        }

        public long Length { get; }

        /// <summary>
        /// Gets the path the reader was opened from. The read policy asks it which volume the file sits on,
        /// so the storage figures a pass starts with come from what that volume has already served.
        /// </summary>
        public string Path { get; }

        // Ranges fetched up front, kept sorted by start. The walk knows every cluster it needs before
        // it begins, and one read at a time is what makes extraction slow on network storage: measured
        // on the user's NAS, one read costs 12,8 ms no matter how big it is, and a pass needs ~2 000 of
        // them, so ~26 s of the 61 s pass was pure waiting between reads. Issuing them together turns
        // that into a few seconds of overlapped waiting, and the bytes are the same handful of megabytes.
        private readonly List<(long Start, byte[] Bytes)> _ahead = new();

        // A rolling window for walks, which visit clusters in file order. Reading one small window per
        // cluster is a round trip per cluster - measured on a real episode: 1 533 reads of 4,7 KB each
        // for a single pass, ~19 s of pure waiting on the user's NAS, because a walk that steps through
        // the file gets no benefit at all from a bigger window per read. One big read per chunk covers
        // every cluster inside it and the walk then costs parsing.
        private long _seqStart = -1;
        private byte[]? _seqBytes;

        /// <summary>Bytes requested from the file so far.</summary>
        public long BytesRead { get; private set; }

        /// <summary>Number of read calls so far.</summary>
        public int ReadCalls { get; private set; }

        /// <summary>
        /// Fetches whole ranges in parallel, so the pass that follows reads from memory instead of waiting
        /// a round trip per cluster. Ranges already in hand are never fetched again, and what was fetched
        /// is registered in the ledger, which is what makes "no byte is read twice" checkable.
        /// </summary>
        /// <param name="ranges">Byte ranges to fetch, in file order; overlaps are harmless.</param>
        /// <param name="parallelism">How many reads to keep in flight.</param>
        /// <param name="cancellationToken">Cancels the prefetch.</param>
        public void Prefetch(IReadOnlyList<(long Start, int Length)> ranges, int parallelism, CancellationToken cancellationToken)
        {
            if (ranges.Count == 0)
            {
                return;
            }

            var workers = Math.Clamp(parallelism, 1, 32);
            var buckets = new List<(long Start, byte[] Bytes)>[workers];
            for (var i = 0; i < workers; i++)
            {
                buckets[i] = new List<(long Start, byte[] Bytes)>();
            }

            var handle = _stream.SafeFileHandle;
            var fetched = 0L;
            var calls = 0;

            // RandomAccess.Read is position based and safe from several threads, which is what lets
            // these run together on one file handle.
            try
            {
                Parallel.For(0, workers, new ParallelOptions { CancellationToken = cancellationToken }, worker =>
                {
                    var mine = buckets[worker];
                    for (var i = worker; i < ranges.Count; i += workers)
                    {
                        var (start, length) = ranges[i];
                        if (length <= 0 || start < 0 || start >= Length)
                        {
                            continue;
                        }

                        var count = (int)Math.Min(length, Length - start);
                        var buffer = new byte[count];
                        var read = RandomAccess.Read(handle, buffer, start);
                        if (read <= 0)
                        {
                            continue;
                        }

                        if (read < count)
                        {
                            Array.Resize(ref buffer, read);
                        }

                        // Recorded on this thread and kept with the bytes it came from: the ledger is the
                        // record of what the pass has paid for, and a fetch that no read uses is waste it names.
                        lock (Ledger)
                        {
                            Ledger.RecordFetchedRange(start, read);
                        }

                        mine.Add((start, buffer));
                        Interlocked.Add(ref fetched, read);
                        Interlocked.Increment(ref calls);
                    }
                });
            }
            catch (AggregateException aggregate)
            {
                // A read that throws inside the loop leaves Parallel.For as an AggregateException ("One or more
                // errors occurred.") carrying the fault that matters - the share went away, the file was replaced,
                // the handle was closed - so it is unwrapped here: what is logged and what the caller catches is
                // the real exception, and a cancelled prefetch is rethrown as the cancellation it is rather than
                // being mistaken for a fault (B21).
                var real = ExceptionDiagnostics.RootCause(aggregate);
                PluginLog.Warn($"prefetch: {ExceptionDiagnostics.Describe(aggregate)} "
                    + $"— {aggregate.InnerExceptions.Count} fault(s) fetching {ranges.Count} range(s) "
                    + $"of {System.IO.Path.GetFileName(Path)}");
                throw real;
            }

            foreach (var bucket in buckets)
            {
                _ahead.AddRange(bucket);
            }

            _ahead.Sort((a, b) => a.Start.CompareTo(b.Start));
            BytesRead += fetched;
            ReadCalls += calls;
        }

        /// <summary>
        /// Makes sure a whole chunk starting here is in memory, replacing the previous chunk. Meant for
        /// walks, which move forward through the file and never come back.
        /// </summary>
        /// <param name="start">Chunk start.</param>
        /// <param name="length">How much to bring in.</param>
        public void SetSequentialChunk(long start, int length)
        {
            if (_seqBytes is not null && start >= _seqStart && start + length <= _seqStart + _seqBytes.Length)
            {
                return;
            }

            var count = (int)Math.Min(Math.Max(length, 1), Length - start);
            if (count <= 0 || start < 0)
            {
                return;
            }

            var buffer = new byte[count];
            var watch = Stopwatch.StartNew();
            var read = RandomAccess.Read(_stream.SafeFileHandle, buffer, start);
            watch.Stop();
            if (read <= 0)
            {
                _seqStart = -1;
                _seqBytes = null;
                return;
            }

            if (read < count)
            {
                Array.Resize(ref buffer, read);
            }

            _seqStart = start;
            _seqBytes = buffer;
            BytesRead += read;
            ReadCalls++;
            Ledger.RecordDiskRead(start, read);
            Policy?.Observe(read, watch.Elapsed.TotalMilliseconds);
        }

        /// <summary>Serves a range from the bytes fetched up front, when they cover it.</summary>
        /// <param name="position">Where to start.</param>
        /// <param name="destination">Where the bytes go.</param>
        /// <returns>True when the bytes were already in memory.</returns>
        private bool TryReadAhead(long position, Span<byte> destination)
        {
            if (destination.Length == 0)
            {
                return false;
            }

            if (_seqBytes is not null
                && position >= _seqStart
                && position + destination.Length <= _seqStart + _seqBytes.Length)
            {
                _seqBytes.AsSpan((int)(position - _seqStart), destination.Length).CopyTo(destination);
                NoteMemoryServed(position, destination.Length);
                return true;
            }

            if (_ahead.Count == 0)
            {
                return false;
            }

            var low = 0;
            var high = _ahead.Count - 1;
            var found = -1;
            while (low <= high)
            {
                var mid = (low + high) / 2;
                if (_ahead[mid].Start <= position)
                {
                    found = mid;
                    low = mid + 1;
                }
                else
                {
                    high = mid - 1;
                }
            }

            if (found < 0)
            {
                return false;
            }

            var (start, bytes) = _ahead[found];
            if (position + destination.Length > start + bytes.Length)
            {
                return false;
            }

            bytes.AsSpan((int)(position - start), destination.Length).CopyTo(destination);
            NoteMemoryServed(position, destination.Length);
            return true;
        }

        /// <summary>
        /// Counts a read that was answered from memory. It is never billed as a read of the file: the
        /// ledger records what the pass paid for, and the counters describe the file.
        /// </summary>
        /// <param name="position">Where the read would have gone.</param>
        /// <param name="length">How many bytes it asked for.</param>
        private void NoteMemoryServed(long position, int length)
        {
            MemoryServedReads++;
            Ledger.RecordServedFromMemory(position, length);
        }

        /// <summary>
        /// Reads as much as the file has at that position. A short result means the file ends
        /// there (truncated or malformed tail): callers stop instead of throwing, so one bad
        /// element cannot fail a whole extraction.
        /// </summary>
        /// <param name="position">Where to start.</param>
        /// <param name="destination">Where the bytes go.</param>
        /// <returns>How many bytes were read.</returns>
        public int ReadAt(long position, Span<byte> destination)
        {
            if (destination.Length == 0)
            {
                return 0;
            }

            if (position < 0 || position >= Length)
            {
                return 0;
            }

            if (TryReadAhead(position, destination))
            {
                return destination.Length;
            }

            _stream.Position = position;
            var watch = Stopwatch.StartNew();
            var read = 0;
            while (read < destination.Length)
            {
                var got = _stream.Read(destination[read..]);
                if (got <= 0)
                {
                    break;
                }

                read += got;
            }

            watch.Stop();
            BytesRead += read;
            ReadCalls++;
            Ledger.RecordDiskRead(position, read);
            Policy?.Observe(read, watch.Elapsed.TotalMilliseconds);
            return read;
        }

        /// <summary>
        /// Reads a small span, reusing recently read bytes when they cover it. One window
        /// read replaces several element-by-element reads inside the same cluster, which is
        /// what keeps high-latency storage from dominating the extraction.
        /// </summary>
        /// <param name="position">Where to start.</param>
        /// <param name="destination">Where the bytes go.</param>
        /// <returns>How many bytes were available.</returns>
        public int ReadNear(long position, Span<byte> destination)
        {
            if (destination.Length == 0)
            {
                return 0;
            }

            if (TryReadAhead(position, destination))
            {
                return destination.Length;
            }

            var windowSize = Math.Clamp(WindowSize, 512, MaxWindowSize);
            if (destination.Length > windowSize)
            {
                return ReadAt(position, destination);
            }

            if (_windowStart >= 0
                && position >= _windowStart
                && position + destination.Length <= _windowStart + _windowLength)
            {
                _window.AsSpan((int)(position - _windowStart), destination.Length).CopyTo(destination);
                NoteMemoryServed(position, destination.Length);
                return destination.Length;
            }

            var length = (int)Math.Min(windowSize, Length - position);
            if (length < destination.Length)
            {
                return ReadAt(position, destination);
            }

            _windowStart = position;
            _windowLength = ReadAt(position, _window.AsSpan(0, length));
            var available = Math.Min(destination.Length, _windowLength);
            _window.AsSpan(0, available).CopyTo(destination);
            return available;
        }

        /// <summary>Reads a whole element payload in one go (used for indexes).</summary>
        /// <param name="position">Where the payload starts.</param>
        /// <param name="length">How many bytes.</param>
        /// <returns>The payload.</returns>
        public byte[] ReadRegion(long position, long length)
        {
            if (length <= 0)
            {
                return Array.Empty<byte>();
            }

            if (length > MaxIndexBytes)
            {
                throw new InvalidOperationException($"element of {length} bytes is too large to load");
            }

            var buffer = new byte[length];
            var read = ReadAt(position, buffer);
            if (read != length)
            {
                throw new InvalidDataException($"index element truncated: wanted {length} bytes at {position}, got {read}");
            }

            return buffer;
        }

        /// <summary>Reads an unsigned integer element value.</summary>
        /// <param name="position">Value position.</param>
        /// <param name="length">Value length in bytes.</param>
        /// <returns>The value.</returns>
        public ulong ReadUnsigned(long position, long length)
        {
            if (length <= 0 || length > 8)
            {
                return 0;
            }

            Span<byte> buffer = stackalloc byte[8];
            var read = ReadNear(position, buffer[..(int)length]);
            return read <= 0 ? 0 : ReadUnsignedBytes(buffer[..read]);
        }

        /// <summary>Reads an element header (id, size) without touching its payload.</summary>
        /// <param name="position">Header position.</param>
        /// <param name="id">Element id (marker bit included).</param>
        /// <param name="size">Element size, or <see cref="ulong.MaxValue"/> when unknown.</param>
        /// <param name="headerLength">Total header length in bytes.</param>
        /// <returns>True when a header was read.</returns>
        public bool TryReadElementHeaderAt(long position, out ulong id, out ulong size, out int headerLength)
        {
            id = 0;
            size = 0;
            headerLength = 0;

            if (position < 0 || position >= Length)
            {
                return false;
            }

            Span<byte> buffer = stackalloc byte[16];
            var available = (int)Math.Min(buffer.Length, Length - position);
            var got = ReadNear(position, buffer[..available]);
            if (got < 2)
            {
                return false;
            }

            available = got;
            var offset = 0;
            if (!TryReadVint(buffer[..available], ref offset, out id, out _, keepMarker: true))
            {
                return false;
            }

            var idLength = offset;
            if (!TryReadVint(buffer[..available], ref offset, out size, out var unknown))
            {
                return false;
            }

            if (unknown)
            {
                size = ulong.MaxValue;
            }

            headerLength = offset;
            return idLength > 0;
        }
    }
}
