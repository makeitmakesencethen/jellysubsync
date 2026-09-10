"""Use CueRelativePosition to jump straight to the subtitle block.

Real remuxes (ffmpeg and mkvmerge, the two files here) write CueRelativePosition for every
cue point: the exact byte offset of the referenced block inside its cluster. Ignoring it
forced the reader to walk every block of each cue cluster (~150 blocks, mostly audio frames)
to find the subtitle — which is what made extraction take tens of seconds per episode.

With it, one cue point = one small read. Clusters whose cues lack the field (or synthetic
files without it) still go through the enumeration path, so nothing regresses.
"""
p = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Services/MkvSubtitleExtractor.cs'
s = open(p).read()


def rep(old, new, label):
    global s
    assert old in s, 'NOT FOUND: ' + label
    s = s.replace(old, new, 1)


# ---------------------------------------------------------------- cue struct carries both
rep("""        var cues = new List<Cue>();
        var clusterOffsets = new List<long>();

        if (cuesDataStart > 0 && cuesSize > 0 && cuesSize <= MaxIndexBytes)
        {
            var cueBuffer = reader.ReadRegion(cuesDataStart, cuesSize);
            clusterOffsets = ParseCueOffsets(cueBuffer, track.TrackNumber, out var cuePoints);
            stats.CuePoints = cuePoints;
        }""",
"""        var cues = new List<Cue>();
        List<CueRef> cueRefs = new();

        if (cuesDataStart > 0 && cuesSize > 0 && cuesSize <= MaxIndexBytes)
        {
            var cueBuffer = reader.ReadRegion(cuesDataStart, cuesSize);
            cueRefs = ParseCueRefs(cueBuffer, track.TrackNumber, out var cuePoints);
            stats.CuePoints = cuePoints;
        }""", 'cue refs')

rep("""        if (clusterOffsets.Count > 0)
        {""",
"""        if (cueRefs.Count > 0)
        {""", 'cue count guard')

rep("""            var seen = new HashSet<long>();
            var index = 0;
            foreach (var offset in clusterOffsets)
            {
                var position = segmentDataStart + offset;
                if (position < 0 || position >= reader.Length || !seen.Add(position))
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
                if (progress is not null && (index % 16 == 0 || index == clusterOffsets.Count))
                {
                    progress($"reading cluster {index}/{clusterOffsets.Count} from the cue index");
                }

                if (!ReadCluster(reader, position, track, cues, stats, wideWindow: true))
                {
                    reason = "unsupported block encoding";
                    return false;
                }
            }""",
"""            var seen = new HashSet<long>();
            var index = 0;
            foreach (var cueRef in cueRefs)
            {
                var position = segmentDataStart + cueRef.ClusterOffset;
                if (position < 0 || position >= reader.Length || !seen.Add(position * 31 + cueRef.RelativePosition))
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
                if (progress is not null && (index % 16 == 0 || index == cueRefs.Count))
                {
                    progress($"reading subtitle {index}/{cueRefs.Count} ({(stats.BytesRead / 1e6):0.0} MB, {stats.ReadCalls} reads)");
                }

                stats.ClustersVisited++;
                var done = false;

                if (cueRef.RelativePosition >= 0)
                {
                    // The muxer told us exactly where the block is: one small read instead of
                    // walking the cluster's blocks (a remux cluster holds ~150 of them, mostly
                    // audio frames).
                    done = ReadIndexedBlock(reader, position, cueRef, track, cues, stats);
                    if (done)
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
            }""", 'cue loop')

# ---------------------------------------------------------------- the directed read
rep("""    /// <summary>Reads a cluster: block headers first, payloads only for the wanted track.</summary>""",
"""    /// <summary>
    /// Reads the one block a cue point points at, using CueRelativePosition to skip the rest of
    /// the cluster. Returns false when the position does not hold the expected block, so the
    /// caller can fall back to walking the cluster.
    /// </summary>
    /// <param name="reader">File reader.</param>
    /// <param name="clusterPosition">Position of the Cluster element's id.</param>
    /// <param name="cueRef">Cue point (cluster offset, relative block offset, cue time).</param>
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
        if (!reader.TryReadElementHeaderAt(clusterPosition, out var clusterId, out var clusterSize, out var clusterHeader)
            || clusterId != IdCluster
            || clusterSize == ulong.MaxValue)
        {
            return false;
        }

        var blockPosition = clusterPosition + clusterHeader + cueRef.RelativePosition;
        if (blockPosition <= clusterPosition
            || blockPosition >= clusterPosition + clusterHeader + (long)clusterSize
            || blockPosition >= reader.Length)
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
            return ReadBlock(reader, dataStart, dataEnd, cueRef.CueTimeMs, track, cues, false, 0);
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
            && ReadBlock(reader, blockStart, blockEnd, cueRef.CueTimeMs, track, cues, true, duration);
    }

    /// <summary>Reads a cluster: block headers first, payloads only for the wanted track.</summary>""", 'indexed read')

# ---------------------------------------------------------------- cue parsing with rel pos
rep("""    /// <summary>
    /// Reads the cluster offsets the Cues element holds for one track. The whole index is
    /// already in memory, so this costs no further reads — parsing it element by element
    /// straight from the file is what made large indexes slow.
    /// </summary>
    private static List<long> ParseCueOffsets(byte[] payload, ulong trackNumber, out long cuePoints)
    {
        var offsets = new List<long>();""",
"""    /// <summary>
    /// Reads the cue points the Cues element holds for one track: cluster offset, the block's
    /// relative offset inside that cluster when the muxer wrote it, and the cue time. The whole
    /// index is already in memory, so this costs no further reads.
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
    /// Reads the cluster offsets the Cues element holds for one track (legacy shape, kept for
    /// callers that do not need the block offsets).
    /// </summary>
    /// <param name="payload">Cues element body.</param>
    /// <param name="trackNumber">Track whose cue points are wanted.</param>
    /// <param name="cuePoints">Total cue points seen.</param>
    /// <returns>The cluster offsets for that track.</returns>
    private static List<long> ParseCueOffsets(byte[] payload, ulong trackNumber, out long cuePoints)
    {
        var offsets = new List<long>();""", 'parse refs')

# remove the now-duplicated old body of ParseCueOffsets, keep the simple one
start = s.index('    private static List<long> ParseCueOffsets(byte[] payload, ulong trackNumber, out long cuePoints)')
end = s.index('    /// <summary>Turns an element header position into its payload position and size')
s = s[:start] + '''    private static List<long> ParseCueOffsets(byte[] payload, ulong trackNumber, out long cuePoints)
    {
        var refs = ParseCueRefs(payload, trackNumber, out cuePoints);
        return refs.Select(r => r.ClusterOffset).ToList();
    }

''' + s[end:]

# ---------------------------------------------------------------- CueRef type + constant + stat
rep("""    private sealed class Cue
    {""",
"""    /// <summary>
    /// One cue point: where the cluster is, where the block sits inside it (when the muxer
    /// recorded that) and the cue's timecode.
    /// </summary>
    /// <param name="ClusterOffset">Offset of the Cluster element, relative to the segment data.</param>
    /// <param name="RelativePosition">Block offset inside the cluster, or -1 when absent.</param>
    /// <param name="CueTimeMs">Cue timecode in milliseconds.</param>
    private sealed record CueRef(long ClusterOffset, long RelativePosition, long CueTimeMs);

    private sealed class Cue
    {""", 'cue ref type')

rep("""    private const ulong IdCueClusterPosition = 0xF1;""",
"""    private const ulong IdCueClusterPosition = 0xF1;
    private const ulong IdCueRelativePosition = 0xF0;""", 'rel position id')

rep("""    /// <summary>Gets or sets how many cue points the index held (all tracks).</summary>
    public long CuePoints { get; set; }""",
"""    /// <summary>Gets or sets how many cue points the index held (all tracks).</summary>
    public long CuePoints { get; set; }

    /// <summary>Gets or sets how many indexed cue points had to fall back to a cluster walk.</summary>
    public int IndexedMisses { get; set; }""", 'miss counter')

open(p, 'w').write(s)
print('CueRelativePosition fast path installed')
