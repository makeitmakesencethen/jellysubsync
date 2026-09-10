"""Per-cluster adaptive window for the cue-index path.

A cue cluster holds the whole audio stretch (hundreds of frames), so the window is sized to
the cluster: a fraction of it, clamped. That turns ~150 round trips per cluster into a
handful, without reading whole multi-megabyte clusters.
"""
p = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Services/MkvSubtitleExtractor.cs'
s = open(p).read()


def rep(old, new, label):
    global s
    assert old in s, 'NOT FOUND: ' + label
    s = s.replace(old, new, 1)


rep("""    /// <summary>Reads a cluster: block headers first, payloads only for the wanted track.</summary>
    private static bool ReadCluster(BlobReader reader, long clusterPosition, SubtitleTrack track, List<Cue> cues, MkvExtractionStats stats)
    {
        if (!reader.TryReadElementHeaderAt(clusterPosition, out var id, out var size, out var headerLength) || id != IdCluster)
        {
            return false;
        }

        stats.ClustersVisited++;""",
"""    /// <summary>Reads a cluster: block headers first, payloads only for the wanted track.</summary>
    /// <param name="reader">File reader.</param>
    /// <param name="clusterPosition">Cluster header position.</param>
    /// <param name="track">Track being extracted.</param>
    /// <param name="cues">Collected subtitles.</param>
    /// <param name="stats">Cost counters.</param>
    /// <param name="wideWindow">
    /// True when this cluster was named by the cue index, so its blocks are worth reading in
    /// wide windows; false while walking every cluster, where a small window keeps the bytes
    /// down.
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
            // Size the window to the cluster: it holds ~150 blocks (mostly audio frames), and
            // each block header otherwise costs its own device round trip.
            reader.WindowSize = (int)Math.Clamp((long)size / 8, 64 * 1024, 512 * 1024);
        }

        stats.ClustersVisited++;""", 'ReadCluster signature')

rep("""            if (clusterOffsets.Count > 0)
        {
            stats.Method = seekheadHit ? "seekhead-cues" : "cue-index";

            // These clusters contain the subtitle blocks, but also every audio frame of that
            // stretch of the file. Reading a wide window covers many block headers per round
            // trip, which is what keeps a NAS from turning ~150 header reads per cluster into
            // 150 round trips.
            reader.WindowSize = 64 * 1024;
""",
"""            if (clusterOffsets.Count > 0)
        {
            stats.Method = seekheadHit ? "seekhead-cues" : "cue-index";
""", 'drop fixed window')

rep("""                if (!ReadCluster(reader, position, track, cues, stats))
                {
                    reason = "unsupported block encoding";
                    return false;
                }""",
"""                if (!ReadCluster(reader, position, track, cues, stats, wideWindow: true))
                {
                    reason = "unsupported block encoding";
                    return false;
                }""", 'cue path call')

open(p, 'w').write(s)
print('adaptive per-cluster window installed')
