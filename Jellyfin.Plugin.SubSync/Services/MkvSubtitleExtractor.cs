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

    /// <summary>Gets or sets how many cue points the index held (all tracks).</summary>
    public long CuePoints { get; set; }

    /// <summary>Gets or sets how many indexed cue points had to fall back to a cluster walk.</summary>
    public int IndexedMisses { get; set; }

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
            "{0}: {1} clusters, {2} blocks, {3:0.0} MB in {4} reads, {5} ms (locate {6} ms, read {7} ms)",
            Method,
            ClustersVisited,
            SubtitleBlocks,
            BytesRead / 1e6,
            ReadCalls,
            TotalMs,
            LocateMs,
            ReadMs)
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
            BlocksPerSecond > 0
                ? BlocksPerSecond
                : (TotalMs <= 0 ? 0 : SubtitleBlocks / (TotalMs / 1000.0)),
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
        CancellationToken cancellationToken = default)
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
            reader = new BlobReader(stream);
            var result = Extract(
                reader,
                subtitleOrdinal,
                progress,
                out srtText,
                out reason,
                stats,
                alsoExtract,
                alsoResults,
                cancellationToken);
            stats.BytesRead = reader.BytesRead;

        // The summary must carry the same numbers as the live lines; "unknown" where a number
        // belongs is how a report stops being believable.
        FinaliseStats(stats);
            stats.ReadCalls = reader.ReadCalls;
            stats.TotalMs = watch.Elapsed.TotalMilliseconds;
            return result;
        }
        catch (Exception ex)
        {
            Diag(ex.ToString());
            reason = ex.GetType().Name + ": " + ex.Message;
            stats.Method = "failed";
            stats.BytesRead = reader?.BytesRead ?? 0;

        // The summary must carry the same numbers as the live lines; "unknown" where a number
        // belongs is how a report stops being believable.
        FinaliseStats(stats);
            stats.ReadCalls = reader?.ReadCalls ?? 0;
            stats.TotalMs = watch.Elapsed.TotalMilliseconds;
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
    /// <returns>True when at least one requested track was extracted.</returns>
    public static bool TryExtractMany(
        string videoPath,
        IReadOnlyList<int> subtitleOrdinals,
        out Dictionary<int, string> results,
        out string reason,
        out MkvExtractionStats stats,
        CancellationToken cancellationToken = default)
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
            cancellationToken);

        if (ok && primaryText.Length > 0)
        {
            results[primary] = primaryText;
        }

        if (results.Count == 0)
        {
            return false;
        }

        if (!ok)
        {
            reason = $"track {primary} failed ({reason}); {results.Count} other track(s) extracted";
        }

        return true;
    }

    private static bool Extract(
        BlobReader reader,
        int subtitleOrdinal,
        Action<string>? progress,
        out string srtText,
        out string reason,
        MkvExtractionStats stats,
        IReadOnlyList<int>? alsoExtract,
        Dictionary<int, string>? alsoResults,
        CancellationToken cancellationToken = default)
    {
        srtText = string.Empty;
        reason = string.Empty;

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

        if (cueRefs.Count > 0)
        {
            stats.Method = seekheadHit ? "seekhead-cues" : "cue-index";

            var seen = new HashSet<long>();
            var index = 0;
            foreach (var cueRef in cueRefs)
            {
                var position = segmentDataStart + cueRef.ClusterOffset;
                if (position < 0 || position >= reader.Length
                    || !seen.Add(position * 31 + cueRef.RelativePosition))
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

                stats.ClustersVisited++;

                if (cueRef.RelativePosition >= 0)
                {
                    // The muxer recorded exactly where the block sits inside the cluster, so one
                    // small read replaces walking that cluster's ~150 blocks to find it.
                    if (ReadIndexedBlock(reader, position, cueRef, track, cues, stats))
                    {
                        continue;
                    }

                    stats.IndexedMisses++;
                }

                if (!ReadCluster(reader, position, track, cues, stats, wideWindow: true))
                {
                    reason = "unsupported block encoding";
                    return false;
                }
            }
        }
        else
        {
            // No index entries for this track. Walking cluster *headers* is still cheap:
            // payloads are skipped, so a 60 GB remux costs a few megabytes of reads, where
            // handing it back to ffmpeg would cost all 60 GB.
            stats.Method = "metadata-scan";
            reader.WindowSize = 4 * 1024;
            if (!ScanClusters(reader, firstClusterPosition, searchEnd, track, cues, stats, progress, cancellationToken))
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
            // Neighbouring subtitle blocks of different tracks sit next to each other in a cluster,
            // so one wider read serves several of them where 4 KB served one.
            reader.WindowSize = 64 * 1024;

            var wanted = new List<(int Ordinal, SubtitleTrack Track, List<Cue> Cues)>();
            var byCluster = new SortedDictionary<long, List<(int Index, CueRef Ref)>>();

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
                foreach (var reference in extraRefs)
                {
                    if (reference.RelativePosition < 0)
                    {
                        continue; // index without block offsets: not usable in a shared pass
                    }

                    var clusterPosition = segmentDataStart + reference.ClusterOffset;
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
            }

            stats.ClustersVisited += byCluster.Count;
            foreach (var (clusterPosition, bucket) in byCluster)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                if (!TryReadClusterHeader(reader, clusterPosition, out var extraDataStart, out var extraDataEnd)
                    || !TryReadClusterTimecode(reader, extraDataStart, extraDataEnd, out var extraTimecode))
                {
                    continue;
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
            }

            foreach (var (ordinal, _, extraCues) in wanted)
            {
                if (extraCues.Count == 0)
                {
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
        }

        readWatch.Stop();
        stats.ReadMs = readWatch.Elapsed.TotalMilliseconds;

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
    /// <param name="cueRef">Cue point.</param>
    /// <param name="track">Track being extracted.</param>
    /// <param name="cues">Collected subtitles.</param>
    /// <param name="stats">Cost counters.</param>
    /// <returns>True when the block was read.</returns>
    private static bool ReadIndexedBlock(
        BlobReader reader,
        long clusterPosition,
        CueRef cueRef,
        SubtitleTrack track,
        List<Cue> cues,
        MkvExtractionStats stats)
    {
        if (cueRef.RelativePosition < 0
            || !TryReadClusterHeader(reader, clusterPosition, out var clusterDataStart, out var clusterEnd))
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

        return ReadBlockAtRelative(reader, clusterDataStart, clusterEnd, cueRef.RelativePosition, clusterTimecode, track, cues);
    }

    /// <summary>
    /// Reads a cluster's header: where its body starts and where it ends.
    /// </summary>
    /// <param name="reader">File reader.</param>
    /// <param name="clusterPosition">Position of the Cluster element's id.</param>
    /// <param name="clusterDataStart">First byte of the cluster's body.</param>
    /// <param name="clusterEnd">One past the cluster's last byte.</param>
    /// <returns>True when a sized Cluster element is at that position.</returns>
    private static bool TryReadClusterHeader(
        BlobReader reader,
        long clusterPosition,
        out long clusterDataStart,
        out long clusterEnd)
    {
        clusterDataStart = 0;
        clusterEnd = 0;
        if (!reader.TryReadElementHeaderAt(clusterPosition, out var id, out var size, out var headerLength)
            || id != IdCluster
            || size == ulong.MaxValue)
        {
            return false;
        }

        clusterDataStart = clusterPosition + headerLength;
        clusterEnd = clusterDataStart + (long)size;
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
        bool wideWindow = false)
    {
        if (!reader.TryReadElementHeaderAt(clusterPosition, out var id, out var size, out var headerLength) || id != IdCluster)
        {
            return false;
        }

        if (wideWindow && size != ulong.MaxValue)
        {
            // A cue cluster holds the subtitle block plus every audio frame of that stretch
            // (~150 blocks in practice). Sizing the window to the cluster means a handful of
            // round trips instead of one per block.
            reader.WindowSize = (int)Math.Clamp((long)size / 8, 64 * 1024, 512 * 1024);
        }

        stats.ClustersVisited++;
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
    private static bool ScanClusters(
        BlobReader reader,
        long startPosition,
        long endPosition,
        SubtitleTrack track,
        List<Cue> cues,
        MkvExtractionStats stats,
        Action<string>? progress,
        CancellationToken cancellationToken = default)
    {
        var cursor = startPosition;
        var visited = 0;
        endPosition = Math.Min(endPosition, reader.Length);

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
                if ((visited & 0x3F) == 0 && cancellationToken.IsCancellationRequested)
                {
                    return false; // the caller turns this into a cancellation
                }
                if (progress is not null && visited % ProgressEveryClusters == 0)
                {
                    progress($"scanning clusters ({visited} read, {stats.BytesRead / 1e6:0.0} MB, {cues.Count} subtitles found)");
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
        AddCue(cues, clusterTimecode, relative, text, hasDuration, durationTicks, track);
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
        var end = hasDuration && durationTicks > 0 ? start + durationTicks : start + 2000;
        cues.Add(new Cue(start, end, text));
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
                                clusterPosition = (long)ReadUnsignedBytes(payload.AsSpan(positionsCursor, (int)posSize));
                            }
                            else if (posId == IdCueRelativePosition)
                            {
                                relativePosition = (long)ReadUnsignedBytes(payload.AsSpan(positionsCursor, (int)posSize));
                            }

                            positionsCursor = posEnd;
                        }

                        if (cueTrack == trackNumber)
                        {
                            matched = true;
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

    private static string ToSrt(List<Cue> cues)
    {
        var builder = new StringBuilder();
        var index = 1;
        for (var i = 0; i < cues.Count; i++)
        {
            var cue = cues[i];
            var end = cue.EndMs;

            // Without a BlockDuration the end is a guess; never let a guess overrun the
            // next cue.
            if (i + 1 < cues.Count && (end > cues[i + 1].StartMs || cue.EndMs - cue.StartMs == 2000))
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
        public Cue(long startMs, long endMs, string text)
        {
            StartMs = startMs;
            EndMs = endMs;
            Text = text;
        }

        public long StartMs { get; }

        public long EndMs { get; }

        public string Text { get; }
    }

    /// <summary>
    /// Positional reader that counts what it costs. Reads are unbuffered, so seeking never
    /// drags a buffer's worth of unrelated bytes along, and element headers can be read for
    /// the few bytes they occupy.
    /// </summary>
    private sealed class BlobReader
    {
        /// <summary>Largest window the reader may use.</summary>
        private const int MaxWindowSize = 64 * 1024;

        private readonly FileStream _stream;
        private readonly byte[] _window = new byte[MaxWindowSize];
        private long _windowStart = -1;
        private int _windowLength;

        /// <summary>
        /// Gets or sets how much is read in one go. Enumerating the blocks of a cluster the
        /// cue index pointed at wants this large (a cluster holds ~150 blocks, mostly audio
        /// frames, and each one otherwise costs its own round trip); walking cluster headers
        /// in a metadata scan wants it small.
        /// </summary>
        public int WindowSize { get; set; } = 4 * 1024;

        public BlobReader(FileStream stream)
        {
            _stream = stream;
            Length = stream.Length;
        }

        public long Length { get; }

        /// <summary>Bytes requested from the file so far.</summary>
        public long BytesRead { get; private set; }

        /// <summary>Number of read calls so far.</summary>
        public int ReadCalls { get; private set; }

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

            _stream.Position = position;
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

            BytesRead += read;
            ReadCalls++;
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
