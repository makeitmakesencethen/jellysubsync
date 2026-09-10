import re

p = 'Jellyfin.Plugin.SubSync/Services/SubSyncService.cs'
s = open(p).read()


def rep(old, new):
    global s
    assert old in s, 'NOT FOUND: ' + old[:100]
    s = s.replace(old, new, 1)


# ------------------------------------------------------------------- config flag
rep("""    /// <summary>
    /// Gets or sets whether embedded subtitles in Matroska files are read via the file's
    /// cue index instead of being demuxed with ffmpeg. This is a pure speed-up (indexed
    /// reads touch kilobytes instead of the whole file) and falls back to ffmpeg for
    /// anything it cannot handle.
    /// </summary>
    public bool FastMkvExtraction { get; set; } = true;""",
    """    /// <summary>
    /// Gets or sets whether embedded subtitles are read through the container's own index
    /// (Matroska cues, MP4 sample table) instead of being demuxed with ffmpeg. This is a
    /// pure speed-up — indexed reads touch kilobytes instead of the whole file — and falls
    /// back to ffmpeg for anything it cannot handle.
    /// </summary>
    public bool FastIndexedExtraction { get; set; } = true;""")

# ------------------------------------------------------------------ wave policy
rep("""    /// <summary>
    /// Picks the next wave of jobs to run: same batch, same mode, at most one job per media
    /// file, up to <paramref name="limit"/> jobs. Batches never interleave, so queue order
    /// between them is preserved; the one-file-one-slot rule keeps parallel workers off the
    /// same file (two processes on one file would repeat the same audio analysis).
    /// </summary>
    /// <param name="queuedInOrder">Queued jobs in queue order.</param>
    /// <param name="headMode">Mode of the job at the head of the queue.</param>
    /// <param name="headBatchId">Batch id of the job at the head of the queue.</param>
    /// <param name="limit">Maximum number of jobs in the wave.</param>
    /// <returns>The wave, in queue order.</returns>
    public static List<SyncJob> SelectWave(
        IEnumerable<SyncJob> queuedInOrder,
        string headMode,
        string? headBatchId,
        int limit)
    {
        var wave = new List<SyncJob>();
        var claimedItems = new HashSet<Guid>();
        foreach (var candidate in queuedInOrder)
        {
            if (wave.Count >= limit)
            {
                break;
            }

            if (!string.Equals(SyncJobMode.Normalize(candidate.Mode), headMode, StringComparison.Ordinal))
            {
                continue;
            }

            if (headBatchId is null ? candidate.BatchId is not null : candidate.BatchId != headBatchId)
            {
                continue;
            }

            if (!claimedItems.Add(candidate.ItemId))
            {
                continue;
            }

            wave.Add(candidate);
        }

        return wave;
    }""",
    """    /// <summary>
    /// Policy for building a wave: the scheduling rules that depend on the machine and the
    /// files rather than on the queue alone.
    /// </summary>
    public sealed class WavePolicy
    {
        /// <summary>Gets or sets the maximum number of jobs in a wave.</summary>
        public int Limit { get; set; } = 1;

        /// <summary>
        /// Gets or sets a predicate saying whether a second job for the same media file may
        /// join the wave. True when that file's speech analysis is already cached — then the
        /// extra job reads nothing from storage and only burns CPU.
        /// </summary>
        public Func<SyncJob, bool>? CanShareMediaFile { get; set; }

        /// <summary>Gets or sets a predicate identifying the storage volume of a job.</summary>
        public Func<SyncJob, string>? VolumeOf { get; set; }

        /// <summary>
        /// Gets or sets a predicate saying whether a job will read a lot of data (embedded
        /// extraction or an audio analysis without a cached speech signal). Only one such job
        /// runs per volume per wave, otherwise four workers on one disk all crawl together.
        /// </summary>
        public Func<SyncJob, bool>? IsHeavyIo { get; set; }
    }

    /// <summary>
    /// Picks the next wave of jobs to run: same batch, same mode, up to the policy's limit,
    /// with storage-aware gating. Batches never interleave, so queue order between them is
    /// preserved.
    /// </summary>
    /// <param name="queuedInOrder">Queued jobs in queue order.</param>
    /// <param name="headMode">Resolved mode of the job at the head of the queue.</param>
    /// <param name="headBatchId">Batch id of the job at the head of the queue.</param>
    /// <param name="policy">Scheduling policy (limit, media-file sharing, volume gating).</param>
    /// <returns>The wave, in queue order.</returns>
    public static List<SyncJob> SelectWave(
        IEnumerable<SyncJob> queuedInOrder,
        string headMode,
        string? headBatchId,
        WavePolicy policy)
    {
        var wave = new List<SyncJob>();
        var claimedItems = new HashSet<Guid>();
        var busyVolumes = new HashSet<string>(StringComparer.Ordinal);

        foreach (var candidate in queuedInOrder)
        {
            if (wave.Count >= policy.Limit)
            {
                break;
            }

            if (!string.Equals(SyncJobMode.Normalize(candidate.Mode), headMode, StringComparison.Ordinal))
            {
                continue;
            }

            if (headBatchId is null ? candidate.BatchId is not null : candidate.BatchId != headBatchId)
            {
                continue;
            }

            var heavy = policy.IsHeavyIo?.Invoke(candidate) ?? false;
            var volume = policy.VolumeOf?.Invoke(candidate) ?? "unknown";

            if (heavy && busyVolumes.Contains(volume))
            {
                // One heavy reader per volume per wave: queue this behind the current wave
                // instead of making the disk serve both at a fraction of the speed.
                continue;
            }

            if (!claimedItems.Add(candidate.ItemId))
            {
                // Another subtitle of a file already in this wave. Only allowed when that
                // file's speech analysis is cached (no storage read, just CPU).
                if (heavy || policy.CanShareMediaFile?.Invoke(candidate) != true)
                {
                    continue;
                }
            }

            if (heavy)
            {
                busyVolumes.Add(volume);
            }

            wave.Add(candidate);
        }

        return wave;
    }

    /// <summary>
    /// Convenience overload for a plain wave without storage awareness (used by tests).
    /// </summary>
    /// <param name="queuedInOrder">Queued jobs in queue order.</param>
    /// <param name="headMode">Resolved mode.</param>
    /// <param name="headBatchId">Batch id.</param>
    /// <param name="limit">Wave size limit.</param>
    /// <returns>The wave, in queue order.</returns>
    public static List<SyncJob> SelectWave(
        IEnumerable<SyncJob> queuedInOrder,
        string headMode,
        string? headBatchId,
        int limit) =>
        SelectWave(queuedInOrder, headMode, headBatchId, new WavePolicy { Limit = limit });""")

open(p, 'w').write(s)
print('wave policy added')
