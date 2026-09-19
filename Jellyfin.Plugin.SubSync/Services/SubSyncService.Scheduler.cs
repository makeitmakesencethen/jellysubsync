using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SubSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;
using Jellyfin.Data;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Extensions;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// The pump, the wave policy and the walk/read-cost policy.
/// </summary>
/// <remarks>
/// Part of <see cref="SubSyncService"/>, split out of the single file as the C2 cluster of
/// <c>knowledge/SUBSYNCSERVICE_MAP.md</c>. A partial class is one class across files: the fields,
/// the constructor and the call sites are unchanged, so nothing here is a new seam - the state this
/// code shares with the rest of the service is still the service's own fields, declared in the main
/// file.
/// </remarks>
public partial class SubSyncService
{
    /// <summary>
    /// Policy for building a wave: the scheduling rules that depend on the machine and the
    /// files rather than on the queue alone.
    /// </summary>
    public sealed class WavePolicy
    {
        /// <summary>Gets or sets the maximum number of jobs in a wave.</summary>
        public int Limit { get; set; } = 1;

        /// <summary>
        /// Gets or sets a predicate saying whether a second job for the same media file may
        /// join the wave. True once that file's speech analysis is cached — the extra job
        /// then reads nothing from storage and only burns CPU.
        /// </summary>
        public Func<SyncJob, bool>? CanShareMediaFile { get; set; }

        /// <summary>
        /// Gets or sets a predicate saying whether a job may start at all, asked about every
        /// candidate including the first one for a media file. The plugin uses it for "this job's
        /// subtitle is already out of the file, so it has nothing to read": a job that is not ready
        /// waits in the queue instead of holding a worker slot while it reads the file, and instead
        /// of duplicating the read the extraction lane is already doing. Null means "anything may
        /// start", which is what the plain wave tests want.
        /// </summary>
        public Func<SyncJob, bool>? MayStart { get; set; }

        /// <summary>
        /// Gets or sets a predicate saying whether a job needs the file's speech analysis
        /// built (embedded extraction or an uncached audio pass). Used to decide whether a second job
        /// may join the wave for the same file, and - with <see cref="WalkCapOf"/> - to count how many
        /// such jobs a storage-bound volume is already carrying.
        /// </summary>
        public Func<SyncJob, bool>? IsHeavyIo { get; set; }

        /// <summary>
        /// Gets or sets a predicate identifying the storage volume of a job. Used to prefer
        /// spreading a wave over several devices; it never blocks a job from running.
        /// </summary>
        public Func<SyncJob, string>? VolumeOf { get; set; }

        /// <summary>
        /// Gets or sets the volumes already being read by running jobs, so a newly started job
        /// prefers a disk that is idle — a preference only; if the queue has nothing else, the
        /// same volume is used.
        /// </summary>
        public IReadOnlyCollection<string>? InUseVolumes { get; set; }

        /// <summary>
        /// Gets or sets the media files that already have a job running, so a subtitle of such a
        /// file waits for that run (it may then reuse its stored audio analysis) instead of
        /// starting a second read of the same file.
        /// </summary>
        public IReadOnlyCollection<Guid>? InUseItemIds { get; set; }

        /// <summary>
        /// Gets or sets how many media-reading jobs are already running per volume, so a volume that has
        /// measured itself slow can be held below its own ceiling. Null means nothing is running.
        /// </summary>
        public IReadOnlyDictionary<string, int>? HeavyInUseByVolume { get; set; }

        /// <summary>
        /// Gets or sets the ceiling on media-reading jobs for a job's volume, as that volume measured
        /// itself, with the measurement that produced it. <see cref="int.MaxValue"/> as the cap means no
        /// ceiling. The reason travels with the number because the log line that reports a held walk has to
        /// say which case it is in - "nothing measured yet" and "measured storage-bound" both produce a cap
        /// of 2, and a line that cannot tell them apart is what made 2.0.30's silent ceiling so hard to see.
        /// </summary>
        public Func<SyncJob, (int Cap, string Why)>? WalkCapOf { get; set; }
    }

    /// <summary>
    /// Resolves the effective mode for a job's batch. <c>auto</c> (the default) is decided
    /// from the shape of the work: a single subtitle stays sequential, several subtitles of
    /// one file reuse that file's speech analysis, and several files run in parallel with
    /// per-file reuse. Explicit modes are honoured as-is — that is what the manual override
    /// in Settings is for.
    /// </summary>
    /// <param name="head">Job at the head of the queue.</param>
    /// <returns>The effective mode, written back onto the batch's auto jobs.</returns>
    private string ResolveModeForBatch(SyncJob head)
    {
        var mode = NormalizeMode(head.Mode);
        if (mode != SyncJobMode.Auto)
        {
            return mode;
        }

        var batchJobs = _jobs.Values
            .Where(j => head.BatchId is null ? j.BatchId is null : j.BatchId == head.BatchId)
            .Where(j => j.Status is SyncJobStatus.Queued or SyncJobStatus.Running)
            .ToList();

        var files = batchJobs.Select(j => j.ItemId).Distinct().Count();
        var resolved = SyncJobMode.ResolveAuto(batchJobs.Count, files);

        foreach (var job in batchJobs)
        {
            if (NormalizeMode(job.Mode) == SyncJobMode.Auto)
            {
                job.Mode = resolved;
            }
        }

        _logger.LogInformation(
            "Auto mode for batch {Batch}: {Tasks} task(s) across {Files} file(s) -> {Mode}",
            head.BatchId ?? "(standalone)", batchJobs.Count, files, SyncJobMode.Describe(resolved));
        return resolved;
    }

    /// <summary>
    /// Decides whether a job is expected to read the media, and so belongs under its volume's ceiling.
    /// </summary>
    /// <remarks>
    /// One function so the decision can be read and pinned on its own, and so the pieces that feed it are
    /// named: an embedded track is read out of the container; an external sidecar is read only when its ruler
    /// is the film's own audio and that analysis is not cached yet; and a job that has started a media read
    /// mid-run says so, because a rescale against the film's own audio is not visible when the job is queued.
    /// </remarks>
    /// <param name="readsMediaNow">The job has started a pass over the media (the rescale check, an audio retry).</param>
    /// <param name="isExternal">The subtitle being fixed is a sidecar file, not a track in the container.</param>
    /// <param name="usesSpeechCache">The job's mode reuses a cached speech analysis.</param>
    /// <param name="speechCached">That analysis is on disk already.</param>
    /// <param name="rulerIsTheAudio">Nothing in the file can serve as a subtitle reference, so the audio is the ruler.</param>
    /// <returns>True when the job is expected to read the media.</returns>
    public static bool NeedsHeavyIo(
        bool readsMediaNow,
        bool isExternal,
        bool usesSpeechCache,
        bool speechCached,
        bool rulerIsTheAudio)
        => readsMediaNow
            || !isExternal
            || (usesSpeechCache && !speechCached && rulerIsTheAudio);

    /// <summary>
    /// True when a job will read the media: it must extract an embedded subtitle, run a speech analysis whose
    /// result is not cached, or it has already started a pass over the film's own audio.
    /// </summary>
    /// <remarks>
    /// The third input is the one this row (P5-10) is about. The rule used to be `usesSpeechCache(mode) &amp;&amp;
    /// !SpeechIsCached(job)`, which answers "is this file's speech cache cold?" - not "will this job read the
    /// media". A job whose ruler is a sibling subtitle needs no analysis of the film's audio, and its reference
    /// is built from text the process already has, so charging it to the volume's ceiling paid for a read it
    /// never made: measured in the tail of batch 8b8dd2d6 on 2026-09-19, three such jobs of one file - each
    /// served from the extracted-subtitle cache, each 1,3 s of alignment - ran one per wave on a share held at
    /// "1 concurrent media read".
    /// </remarks>
    /// <param name="job">Job under consideration.</param>
    /// <param name="mode">The batch's resolved mode.</param>
    /// <returns>True when the job is expected to read the media.</returns>
    private bool JobNeedsHeavyIo(SyncJob job, string mode)
    {
        if (!_jobContexts.TryGetValue(job.Id, out var ctx))
        {
            return false;
        }

        return NeedsHeavyIo(
            job.ReadsMediaNow,
            ctx.Stream.IsExternal,
            SyncJobMode.UsesSpeechCache(mode),
            SpeechIsCached(job),
            RulerWillBeTheAudio(job));
    }

    /// <summary>
    /// True when this job's ruler will be the film's own audio, because the file carries no embedded text track
    /// the plugin can build a subtitle reference from (P5-10).
    /// </summary>
    /// <remarks>
    /// The same picker the job path uses, over the same list the pipeline uses for an external sidecar
    /// (<c>JobPipeline</c>'s external branch), so the scheduler's prediction and the ruler the job ends up using
    /// cannot drift apart. Memoised per media file: a file's track list does not change while a batch runs, and
    /// the planner asks this for every queued job on every pass. Anything unknown answers "the audio is the
    /// ruler", which is the cautious side - that job is counted as a reader.
    /// </remarks>
    /// <param name="job">Job under consideration.</param>
    /// <returns>True when the audio will be the reference.</returns>
    private bool RulerWillBeTheAudio(SyncJob job)
    {
        if (!_jobContexts.TryGetValue(job.Id, out var ctx)
            || ctx.Video is null
            || string.IsNullOrEmpty(ctx.Video.Path))
        {
            return true;
        }

        var path = ctx.Video.Path;
        if (_rulerIsTheAudio.TryGetValue(path, out var known))
        {
            return known;
        }

        var answer = true;
        try
        {
            var siblings = ctx.Video.GetMediaSources(true)
                .SelectMany(source => source.MediaStreams)
                .Where(stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle
                    && !stream.IsExternal)
                .ToList();
            if (siblings.Count > 0)
            {
                answer = MediaStreamMap.SelectReferenceStream(
                    false,
                    siblings.Select(stream => stream.Codec ?? string.Empty).ToList(),
                    -1,
                    siblings.Select(stream => stream.IsForced).ToList()) is null;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not work out whether {Video} has a sibling text track to align against", path);
            answer = true;
        }

        return _rulerIsTheAudio.GetOrAdd(path, answer);
    }


    /// <summary>
    /// Why a queued job has not started, in words the interface can show.
    /// </summary>
    /// <param name="job">The queued job.</param>
    /// <param name="running">Jobs running right now.</param>
    /// <param name="limit">Worker slots in force.</param>
    /// <returns>The reason.</returns>
    private string QueuedReason(SyncJob job, int running, int limit)
    {
        if (!_jobContexts.TryGetValue(job.Id, out var context))
        {
            return "Queued";
        }

        var path = context.Video?.Path ?? string.Empty;
        if (!context.Stream.IsExternal && path.Length > 0 && !ExtractionReady(job))
        {
            return _passInFlight.ContainsKey(path)
                ? "Reading subtitles from the video \u2014 this one starts as soon as its track is out"
                : "Waiting for this file's subtitles to be read";
        }

        return running >= limit
            ? $"Waiting for a free worker ({running} of {limit} busy)"
            : "Starting\u2026";
    }


    /// <summary>
    /// Picks the next wave of jobs to run: same batch, same mode, up to the policy's limit.
    /// Batches never interleave, so queue order between them is preserved.
    ///
    /// Volumes are a preference, never a filter: the first pass walks the queue and takes
    /// the first job of each distinct volume, so parallel work spreads over the storage you
    /// have; the second pass then fills the remaining slots with whatever is left, whatever
    /// volume it sits on. A queue that lives entirely on one disk therefore still runs at
    /// full width — it simply has nothing to spread over.
    /// </summary>
    /// <param name="queuedInOrder">Queued jobs in queue order.</param>
    /// <param name="headMode">Resolved mode at the head of the queue.</param>
    /// <param name="headBatchId">Batch id of the job at the head of the queue.</param>
    /// <param name="policy">Scheduling policy (limit, media sharing, volume preference).</param>
    /// <returns>The wave, in queue order.</returns>
    public static List<SyncJob> SelectWave(
        IEnumerable<SyncJob> queuedInOrder,
        string headMode,
        string? headBatchId,
        WavePolicy policy)
    {
        var candidates = new List<SyncJob>();
        foreach (var candidate in queuedInOrder)
        {
            if (!string.Equals(SyncJobMode.Normalize(candidate.Mode), headMode, StringComparison.Ordinal))
            {
                continue;
            }

            if (headBatchId is null ? candidate.BatchId is not null : candidate.BatchId != headBatchId)
            {
                continue;
            }

            candidates.Add(candidate);

            // The scan has to be wide enough to *find* a wave, not merely to hold one. A fixed
            // multiple of the limit is not: with ten-track episodes, limit*4 candidates covered
            // only two distinct media files, so a four-worker batch ran two wide and reported
            // "using 2/4 workers". The ceiling is only there to keep one scheduler pass bounded.
            if (candidates.Count >= Math.Min(20000, Math.Max(policy.Limit * 64, 512)))
            {
                break;
            }
        }

        var wave = new List<SyncJob>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var claimedItems = new HashSet<Guid>(policy.InUseItemIds ?? Array.Empty<Guid>());
        var usedVolumes = new HashSet<string>(
            policy.InUseVolumes ?? Array.Empty<string>(),
            StringComparer.Ordinal);
        var heavyInWave = new Dictionary<string, int>(StringComparer.Ordinal);

        foreach (var candidate in candidates)
        {
            if (wave.Count >= policy.Limit)
            {
                break;
            }

            var volume = policy.VolumeOf?.Invoke(candidate) ?? "unknown";
            if (usedVolumes.Contains(volume))
            {
                continue; // a job from this volume is already in the wave — try to spread
            }

            if (!TryTake(candidate, policy, claimedItems))
            {
                continue;
            }

            if (!HasRoomOnItsVolume(candidate, policy, heavyInWave))
            {
                continue;
            }

            usedVolumes.Add(volume);
            taken.Add(candidate.Id);
            ClaimHeavy(candidate, policy, heavyInWave);
            wave.Add(candidate);
        }

        // Second pass: no spreading left to do, so fill the wave to its full width.
        foreach (var candidate in candidates)
        {
            if (wave.Count >= policy.Limit)
            {
                break;
            }

            if (taken.Contains(candidate.Id))
            {
                continue;
            }

            if (!TryTake(candidate, policy, claimedItems))
            {
                continue;
            }

            if (!HasRoomOnItsVolume(candidate, policy, heavyInWave))
            {
                continue;
            }

            ClaimHeavy(candidate, policy, heavyInWave);
            wave.Add(candidate);
        }

        return wave.OrderBy(j => j.BatchIndex).ToList();
    }


    /// <summary>
    /// Decides which queued jobs may start right now, given how many are already running.
    ///
    /// This is the difference between a worker pool and a group scheduler: the available slots
    /// are what remains of the limit, and every finished job frees one immediately instead of
    /// waiting for the rest of its group.
    /// </summary>
    /// <param name="queuedInOrder">Queued jobs, in queue order.</param>
    /// <param name="running">Jobs currently running.</param>
    /// <param name="headMode">Resolved mode of the batch at the head of the queue.</param>
    /// <param name="headBatchId">Batch of the job at the head of the queue.</param>
    /// <param name="limit">How many jobs may run at once.</param>
    /// <param name="volumeOf">Volume lookup for a job.</param>
    /// <param name="isHeavyIo">Whether a job needs the file's audio analysis.</param>
    /// <param name="canShareMediaFile">Whether a second job may run for an already-claimed file.</param>
    /// <param name="mayStart">
    /// Whether a job may start at all, asked about every candidate. The plugin passes "this job's
    /// subtitle is already extracted"; null means anything may start.
    /// </param>
    /// <param name="walkCapOf">Ceiling on media-reading jobs for a job's volume, with its reason.</param>
    /// <returns>The jobs to start, in queue order (empty when every slot is busy).</returns>
    public static List<SyncJob> PlanStart(
        IEnumerable<SyncJob> queuedInOrder,
        IReadOnlyCollection<SyncJob> running,
        string headMode,
        string? headBatchId,
        int limit,
        Func<SyncJob, string> volumeOf,
        Func<SyncJob, bool> isHeavyIo,
        Func<SyncJob, bool> canShareMediaFile,
        Func<SyncJob, bool>? mayStart = null,
        Func<SyncJob, (int Cap, string Why)>? walkCapOf = null)
    {
        var slots = limit - running.Count;
        if (slots <= 0)
        {
            return new List<SyncJob>();
        }

        var runningVolumes = running.Select(volumeOf).ToList();
        var runningItems = running.Select(j => j.ItemId).ToList();
        var heavyByVolume = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var job in running)
        {
            if (!isHeavyIo(job))
            {
                continue;
            }

            var key = volumeOf(job);
            heavyByVolume[key] = (heavyByVolume.TryGetValue(key, out var n) ? n : 0) + 1;
        }


        return SelectWave(
            queuedInOrder,
            headMode,
            headBatchId,
            new WavePolicy
            {
                Limit = slots,
                VolumeOf = volumeOf,
                InUseVolumes = runningVolumes,
                InUseItemIds = runningItems,
                IsHeavyIo = isHeavyIo,
                CanShareMediaFile = canShareMediaFile,
                MayStart = mayStart,
                HeavyInUseByVolume = heavyByVolume,
                WalkCapOf = walkCapOf
            });
    }

    /// <summary>
    /// Says whether a media-reading candidate may join the wave without exceeding its volume's ceiling.
    /// A volume nothing has measured has no ceiling, which is the case for every setup this plugin runs on
    /// today; only a volume whose own reads have shown it storage-bound is held below the worker count, and
    /// that ceiling counts that volume alone, so jobs on other volumes are untouched by it.
    /// </summary>
    /// <param name="candidate">Job under consideration.</param>
    /// <param name="policy">Scheduling policy, whose <see cref="WavePolicy.WalkCapOf"/> sets the ceiling.</param>
    /// <param name="heavyInWave">Media reads this wave has already claimed, per volume.</param>
    /// <returns>True when the volume has room for this read.</returns>
    private static bool HasRoomOnItsVolume(
        SyncJob candidate, WavePolicy policy, Dictionary<string, int> heavyInWave)
    {
        if (policy.IsHeavyIo?.Invoke(candidate) != true)
        {
            return true; // not a media read: the worker count remains its only bound
        }

        var ceiling = policy.WalkCapOf?.Invoke(candidate) ?? (int.MaxValue, "no ceiling is configured");
        if (ceiling.Cap >= int.MaxValue)
        {
            return true;
        }

        if (ceiling.Cap <= 0)
        {
            return false;
        }

        var volume = policy.VolumeOf?.Invoke(candidate) ?? "unknown";
        var running = policy.HeavyInUseByVolume is not null
            && policy.HeavyInUseByVolume.TryGetValue(volume, out var inUse)
                ? inUse
                : 0;
        heavyInWave.TryGetValue(volume, out var inWave);
        if (running + inWave >= ceiling.Cap)
        {
            LogCeilingHeld(volume, ceiling.Cap, ceiling.Why);
            return false;
        }

        return true;
    }


    /// <summary>
    /// Says, once a minute per volume, that a walk is waiting on that volume's ceiling. Without this line a
    /// capped run and an uncapped one look identical in the log - which is exactly how the first release of
    /// this ceiling hid that it was never applying.
    /// </summary>
    private static void LogCeilingHeld(string volume, int cap, string why)
    {
        var now = DateTime.UtcNow;
        if (_ceilingLogged.TryGetValue(volume, out var last) && now - last < TimeSpan.FromSeconds(60))
        {
            return;
        }

        _ceilingLogged[volume] = now;
        Services.PluginLog.Info($"walk ceiling: holding {volume} at {cap} concurrent media read(s) - {why}");
    }

    /// <summary>Counts a candidate that joined the wave against its volume's ceiling.</summary>
    private static void ClaimHeavy(SyncJob candidate, WavePolicy policy, Dictionary<string, int> heavyInWave)
    {
        if (policy.IsHeavyIo?.Invoke(candidate) != true)
        {
            return;
        }

        var volume = policy.VolumeOf?.Invoke(candidate) ?? "unknown";
        heavyInWave[volume] = (heavyInWave.TryGetValue(volume, out var n) ? n : 0) + 1;
    }

    /// <summary>
    /// Decides whether a candidate may join the wave, claiming its media file when it does.
    /// Several subtitles of one file may share a wave only when that file's speech analysis
    /// is cached, so two workers never race to build the same cache entry.
    /// </summary>
    /// <param name="candidate">Job under consideration.</param>
    /// <param name="policy">Scheduling policy.</param>
    /// <param name="claimedItems">Media files already represented in the wave.</param>
    /// <returns>True when the candidate joins the wave.</returns>
    private static bool TryTake(SyncJob candidate, WavePolicy policy, HashSet<Guid> claimedItems)
    {
        // The file is claimed either way, so a refusal here still stops a second job of the same file
        // from slipping in behind it.
        var alreadyInWave = !claimedItems.Add(candidate.ItemId);

        // Asked about every candidate, first job of a file included: a job that is not ready must not
        // start at all. Letting the first one through on the old rule ("is the file free?") opened a
        // hole big enough for it to read the whole file for itself - measured on a real episode, four
        // such passes of 590-870 MB each while the extraction lane was reading the same file.
        if (policy.MayStart is not null && !policy.MayStart(candidate))
        {
            return false;
        }

        if (alreadyInWave && policy.CanShareMediaFile?.Invoke(candidate) != true)
        {
            // The policy decides whether a second subtitle of the same file may run alongside the
            // first — it is the only thing that knows whether that file's analysis is stored.
            return false;
        }

        return true;
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
        SelectWave(queuedInOrder, headMode, headBatchId, new WavePolicy { Limit = limit });


    /// <summary>
    /// Gets a media file's length in bytes, or 0 when it cannot be read - never throws, because this is called
    /// on the path of a job that has just succeeded and must not be turned into a failure by a stat.
    /// </summary>
    /// <param name="path">Path to the media file.</param>
    /// <returns>Bytes, or 0.</returns>
    internal static long MediaLengthOf(string? path)
    {
        try
        {
            return string.IsNullOrWhiteSpace(path) ? 0 : new FileInfo(path).Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>
    /// The ceiling on concurrent media-reading jobs for the volume a path lives on, from everything that
    /// volume has measured - its reads and its own walks. A fast volume means no ceiling at all, which is the
    /// behaviour every setup that is not storage-bound keeps once the volume has measured itself. A path whose
    /// volume cannot be worked out is treated like an unmeasured one: not known to be fast is not the same as
    /// fast, and the alternative is what let eight walks onto one share on 2026-09-14.
    /// </summary>
    /// <param name="path">Path to a job's media file.</param>
    /// <returns>The ceiling and the measurement behind it.</returns>
    public static (int Cap, string Why) WalkCapOfPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return (UnmeasuredWalkCap,
                "this job's volume could not be worked out, so it is treated as slow until it measures fast");
        }

        var profile = Services.VolumeProfiles.For(path);
        var cap = WalkCapForProfile(
            profile.MsPerCall(),
            profile.WalkBytesPerMs(),
            Services.VolumeProfiles.FastestReadMsPerCall(profile.Key),
            Services.VolumeProfiles.FastestWalkBytesPerMs(profile.Key),
            profile.LastCeiling,
            profile.SampleCount);
        profile.RememberCeiling(cap.Cap);
        return cap;
    }


    /// <summary>
    /// Where the index-th read of a volume's first measurement starts.
    /// </summary>
    /// <remarks>
    /// Spread over the file rather than repeated at one place: a page cache that holds one region must not
    /// provide all of the samples that decide the volume. A file too small to hold three separated reads is
    /// read from its start, where the samples still differ by what the storage did between them.
    /// </remarks>
    /// <param name="length">The file's length in bytes.</param>
    /// <param name="index">Which read of the set this is.</param>
    /// <param name="reads">How many reads the set holds.</param>
    /// <returns>The offset to read at.</returns>
    private static long ProbeOffset(long length, int index, int reads)
    {
        if (length <= ProbeBytes * (reads + 1))
        {
            return 0;
        }

        return (long)((length - ProbeBytes) * ((index + 1.0) / (reads + 1.0)));
    }

    /// <summary>
    /// Gives a volume that has nothing measured about it its first measurement, once per process.
    /// </summary>
    /// <remarks>
    /// Taken in the job's own thread and never while the queue lock is held: the scheduler must not touch
    /// storage on its way to a decision. <see cref="ProbeReads"/> 16 KB reads are taken at separated offsets and
    /// the median of them is what the volume is classified by - one read cannot tell a volume's steady state,
    /// and on 2026-09-14 a single cold read of 231 ms against a steady 13-46 ms held a share to one walk for a
    /// whole run (S41). Every read is fed to the profile, so the figure the ceiling is decided from is the
    /// median of these and the line says what the median stands on.
    /// </remarks>
    /// <param name="videoPath">The media file, which names the volume, or null when it is not known yet.</param>
    private void ProbeVolumeIfUnmeasured(string? videoPath)
    {
        if (string.IsNullOrWhiteSpace(videoPath))
        {
            return;
        }

        var profile = Services.VolumeProfiles.For(videoPath);
        if (!profile.NeedsProbe || !profile.TryBeginProbe())
        {
            return;
        }

        try
        {
            var length = MediaLengthOf(videoPath);
            if (length <= 0)
            {
                return;
            }

            var buffer = new byte[ProbeBytes];
            using var stream = new FileStream(
                videoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, ProbeBytes, FileOptions.RandomAccess);

            var samples = new List<double>(ProbeReads);
            var bytesRead = 0;
            for (var index = 0; index < ProbeReads; index++)
            {
                var offset = ProbeOffset(length, index, ProbeReads);
                if (offset > 0)
                {
                    stream.Seek(offset, SeekOrigin.Begin);
                }

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var read = stream.Read(buffer, 0, ProbeBytes);
                watch.Stop();
                if (read <= 0)
                {
                    break;
                }

                bytesRead = read;
                // Every read is fed to the profile, so the figure the ceiling is decided from is the median of
                // these and not the first one - the defect S41 was: one cold read became the volume's class.
                var ms = Math.Max(watch.Elapsed.TotalMilliseconds, 0.001);
                samples.Add(ms);
                profile.Observe(read, ms);
            }

            if (samples.Count == 0)
            {
                return;
            }

            samples.Sort();
            var median = samples[samples.Count / 2];
            var cap = WalkCapOfPath(videoPath);
            PluginLog.Info(
                $"volume {profile.Key} had nothing measured about it, so it was read {samples.Count} time(s) of "
                + $"{bytesRead / 1024} KB: median of {samples.Count} reads took {median:0.00} ms "
                + $"(slowest {samples[^1]:0.00} ms) - the ceiling for that volume is "
                + $"{(cap.Cap >= int.MaxValue ? "none" : cap.Cap.ToString())} ({cap.Why})");
        }
        catch (Exception ex)
        {
            PluginLog.Info(
                $"volume {profile.Key} could not be read for its first measurement ({ex.GetType().Name}); it stays "
                + "unmeasured and conservatively held");
        }
    }

    /// <summary>
    /// Counts how many of the volumes given are not the one a walk just measured.
    /// </summary>
    /// <remarks>
    /// A walk's throughput is the storage's speed *and* everything else that happened to be running: measured
    /// on 2026-09-14, the same local NVMe volume walked at 85-91 MB/s with only its own jobs in flight and at
    /// 45-58 MB/s while a slow share was being walked at the same time. The second number is a statement about
    /// the moment, not about the disk, and feeding it to the ceiling held a fast volume to two concurrent
    /// walks for the rest of a mixed batch.
    /// </remarks>
    /// <param name="thisVolume">The volume the walk measured.</param>
    /// <param name="otherVolumes">The volumes of every other job in flight.</param>
    /// <returns>How many of them are elsewhere (each job counts once).</returns>
    internal static int VolumesOtherThan(string thisVolume, IEnumerable<string> otherVolumes)
        => otherVolumes.Count(v => !string.Equals(v, thisVolume, StringComparison.Ordinal));


    /// <summary>
    /// Gets the ceiling for a volume from a measured cost per read, with no reference to compare against.
    /// </summary>
    /// <param name="msPerCall">Milliseconds per read, or null when nothing has read from this volume.</param>
    /// <returns>The ceiling and the measurement behind it.</returns>
    public static (int Cap, string Why) WalkCapForProfile(double? msPerCall)
        => WalkCapForProfile(msPerCall, null);

    /// <summary>
    /// Gets the ceiling for a volume from every signal it has, taking the more conservative of them: a read
    /// sample describes the latency it was read at, a walk describes the throughput the media actually moved
    /// at, and either one saying "storage-bound" is enough to hold the volume.
    /// </summary>
    /// <remarks>
    /// Two signals exist because one of them can be missing for a whole run. Reads only happen when the
    /// extraction path reads, and on a warm subtitle cache it does not read at all - so a fast volume could
    /// show no samples for ever and sit at the conservative cap. The walk always happens, so it always measures.
    ///
    /// A volume nothing has measured is held at <see cref="UnmeasuredWalkCap"/> rather than treated as fast.
    /// Measured on 2026-09-14: with "no measurement means no ceiling", a whole season's eight walks were
    /// admitted in one wave before the first extraction pass had reported anything, so the ceiling was never
    /// asked again and all eight walked the share at once (max concurrent 8, 5,5-7,0 min each). The store is
    /// filled *during* the pass that reads the file and the wave that matters is planned *before* it, so the
    /// unmeasured case has to be the conservative one. It costs a fast volume at most its first wave at two,
    /// and lifts as soon as that volume's walk or its reads measure it fast.
    /// </remarks>
    /// <param name="msPerCall">Milliseconds per read, or null when nothing has read from this volume.</param>
    /// <param name="walkBytesPerMs">Bytes per millisecond a walk showed, or null when no walk has finished.</param>
    /// <returns>The ceiling and the measurement behind it.</returns>
    public static (int Cap, string Why) WalkCapForProfile(double? msPerCall, double? walkBytesPerMs)
        => WalkCapForProfile(msPerCall, walkBytesPerMs, null, null, null);

    /// <summary>
    /// Gets the ceiling for a volume from everything measured about it *and* everything measured about this
    /// machine, which is what makes the judgement portable: nothing here is a throughput or a latency that only
    /// one machine's hardware produces.
    /// </summary>
    /// <remarks>
    /// The walks decide when the volume has walks of its own, because walking is the operation being limited.
    /// The read side decides only when there are no walks to go on - a volume whose reads are slow but whose own
    /// walks are fine must not be over-capped, which is exactly what held a local NVMe to two walks on
    /// 2026-09-14. With a reference available the band between the bound and the release fraction keeps a
    /// decision sticky; with no reference (nothing else on this machine has enough samples) the documented
    /// absolute behaviour is used instead, so a process that has measured one volume behaves as it always did.
    /// </remarks>
    /// <param name="msPerCall">Milliseconds per read, or null when nothing has read from this volume.</param>
    /// <param name="walkBytesPerMs">Bytes per millisecond a walk showed, or null when no walk has finished.</param>
    /// <param name="referenceMsPerCall">The machine's best read latency, or null.</param>
    /// <param name="referenceBytesPerMs">The machine's best walk throughput, or null.</param>
    /// <param name="previousCap">The ceiling this volume was last held to, or null.</param>
    /// <param name="readSamples">
    /// Reads the volume's per-read figure is a median of, or null when the caller has no sample count - a
    /// median of one is a single observation, and only a backing of <see cref="ThrashTierMinReads"/> reads
    /// or more lets it call the volume thrashing.
    /// </param>
    /// <returns>The ceiling and the measurement behind it.</returns>
    public static (int Cap, string Why) WalkCapForProfile(
        double? msPerCall,
        double? walkBytesPerMs,
        double? referenceMsPerCall,
        double? referenceBytesPerMs,
        int? previousCap,
        int? readSamples = null)
    {
        if (walkBytesPerMs is { } walked && referenceBytesPerMs is { } bestWalk && bestWalk > 0)
        {
            var ratio = walked / bestWalk;
            var share = $"{(walked / 1000.0):0.0} MB/s against the best {bestWalk / 1000.0:0.0} MB/s this machine has measured ({ratio:0.00}x)";
            if (ratio < WalkBoundFraction)
            {
                return (StorageBoundWalkCap, $"this volume's last walk moved {share}, which is storage-bound");
            }

            if (ratio >= WalkReleaseFraction)
            {
                return (int.MaxValue, $"this volume's last walk moved {share}, which is fast");
            }

            return previousCap is { } held
                ? (held, $"this volume's last walk moved {share}, inside the band between storage-bound and fast, so the last decision for it stands ({CapName(held)})")
                : (StorageBoundWalkCap, $"this volume's last walk moved {share}, inside the band and nothing has been decided for it yet, so it is held conservatively");
        }

        if (msPerCall is { } ms && referenceMsPerCall is { } bestRead && bestRead > 0)
        {
            var ratio = ms / bestRead;
            var share = $"{ms:0.00} ms per read against the best {bestRead:0.00} ms this machine has measured ({ratio:0.0}x)";
            if (ratio >= ThrashingReadRatio && Backed(readSamples))
            {
                return (1, $"this volume's reads measure {share}{Backing(readSamples)}, which is thrashing");
            }

            if (ratio >= ThrashingReadRatio)
            {
                return (StorageBoundWalkCap,
                    $"this volume's reads measure {share}, but that is {Backing(readSamples)?.TrimStart(',', ' ')} "
                    + $"and one read is not a steady state, so it is held at {StorageBoundWalkCap} until the volume "
                    + "has been read again");
            }

            if (ratio >= SlowReadRatio)
            {
                return (StorageBoundWalkCap,
                    $"this volume's reads measure {share}{Backing(readSamples)}, which is storage-bound");
            }

            if (ratio <= FastReadRatio)
            {
                return (int.MaxValue, $"this volume's reads measure {share}{Backing(readSamples)}, which is fast");
            }

            return previousCap is { } heldReads
                ? (heldReads, $"this volume's reads measure {share}, inside the band, so the last decision for it stands ({CapName(heldReads)})")
                : (StorageBoundWalkCap, $"this volume's reads measure {share}, inside the band and nothing has been decided for it yet, so it is held conservatively");
        }

        // No reference to compare against: the absolute behaviour, unchanged, for a process that has not
        // measured a second volume yet.
        (int Cap, string Why)? byReads = msPerCall is null ? null : CapFromReadCost(msPerCall.Value, readSamples);
        (int Cap, string Why)? byWalk = walkBytesPerMs is null ? null : CapFromWalkThroughput(walkBytesPerMs.Value);

        if (byReads is null && byWalk is null)
        {
            return (UnmeasuredWalkCap,
                "nothing has measured this volume yet, so it is treated as slow until something does");
        }

        if (byReads is null)
        {
            return byWalk!.Value;
        }

        if (byWalk is null)
        {
            return byReads.Value;
        }

        // Both measured. The more conservative wins - a volume whose reads were fast but whose walks are slow
        // is storage-bound for the walks, which is what this ceiling exists to limit.
        return byReads.Value.Cap <= byWalk.Value.Cap ? byReads.Value : byWalk.Value;
    }

    private static string CapName(int cap) => cap >= int.MaxValue ? "none" : cap.ToString();

    private static (int Cap, string Why) CapFromReadCost(double msPerCall, int? readSamples)
    {
        if (msPerCall >= ThrashingReadMsPerCall && Backed(readSamples))
        {
            return (1, $"this volume measured {msPerCall:0.0} ms per read{Backing(readSamples)}, which is thrashing");
        }

        if (msPerCall >= ThrashingReadMsPerCall)
        {
            return (StorageBoundWalkCap,
                $"this volume measured {msPerCall:0.0} ms on {Backing(readSamples)?.TrimStart(',', ' ')} and nothing "
                + $"else, which is not a steady state, so it is held at {StorageBoundWalkCap} until the volume has "
                + "been read again");
        }

        if (msPerCall >= SlowReadMsPerCall)
        {
            return (StorageBoundWalkCap,
                $"this volume measured {msPerCall:0.0} ms per read{Backing(readSamples)}, which is storage-bound");
        }

        return (int.MaxValue,
            $"this volume measured {msPerCall:0.00} ms per read{Backing(readSamples)}, which is fast");
    }

    /// <summary>Whether enough reads stand behind a per-read figure for it to reach the thrash tier.</summary>
    /// <param name="readSamples">How many reads the figure is a median of, or null when that is not tracked.</param>
    /// <returns>True when the figure may be believed about the volume's steady state.</returns>
    private static bool Backed(int? readSamples) => readSamples is null || readSamples >= ThrashTierMinReads;

    /// <summary>Names the backing of a per-read figure, for the log line that quotes it.</summary>
    /// <param name="readSamples">How many reads the figure is a median of, or null when that is not tracked.</param>
    /// <returns>A fragment to append to the reason, empty when the backing is unknown.</returns>
    private static string Backing(int? readSamples) => readSamples switch
    {
        null => string.Empty,
        0 => string.Empty,
        1 => ", but that is one read",
        _ => $", over {readSamples} read(s)",
    };

    private static (int Cap, string Why) CapFromWalkThroughput(double bytesPerMs)
    {
        var mbPerSec = bytesPerMs / 1000.0;

        if (mbPerSec < ThrashingWalkMbPerSec)
        {
            return (1, $"this volume's last walk moved {mbPerSec:0.0} MB/s, which is thrashing");
        }

        if (mbPerSec < FastWalkMbPerSec)
        {
            return (2, $"this volume's last walk moved {mbPerSec:0.0} MB/s, which is storage-bound");
        }

        return (int.MaxValue, $"this volume's last walk moved {mbPerSec:0.0} MB/s, which is fast");
    }


    /// <summary>Worker count for parallel mode, clamped to the documented range.</summary>
    /// <param name="workers">Configured worker count.</param>
    /// <returns>The count actually used, which is the configured one for every value in range.</returns>
    public static int NormalizeWorkers(int workers) =>
        workers < 1 ? 1 : (workers > MaxParallelWorkers ? MaxParallelWorkers : workers);


    /// <summary>
    /// Logs the worker limit whenever it changes, so the value in force can be read from the log
    /// instead of being inferred from behaviour.
    /// </summary>
    private void LogWorkerLimit()
    {
        var limit = EffectiveWorkerLimit;
        if (limit == _lastLoggedWorkerLimit)
        {
            return;
        }

        _lastLoggedWorkerLimit = limit;
        _logger.LogInformation(
            "SubSync worker limit is {Limit} (configured: {Configured}, ceiling: {Ceiling})",
            limit,
            ConfiguredWorkerLimit,
            MaxParallelWorkers);
    }


    private void WakePump()
    {
        // The lane is started first, and that order matters: the scheduler refuses to start a job whose
        // subtitle is not extracted yet and reads "no lane at all" as "the lane is gone, so let the job
        // extract for itself". Waking the pump before the lane existed opened that hatch on the very
        // first plan, and three jobs of a real episode each went and read ~600 MB for themselves while
        // the lane was reading the same file.
        WakeExtractor();

        lock (_queueLock)
        {
            if (_pumpTask is null || _pumpTask.IsCompleted)
            {
                _pumpTask = Task.Run(PumpAsync);
            }
        }

        try { _wakePump.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
    }

    /// <summary>
    /// Runs the queue with a fixed number of slots rather than in groups.
    ///
    /// A group (wave) model made every worker wait for the slowest job in its group and then
    /// start the next step together, which wasted the finished workers' time and hit the disk in
    /// bursts. Here a worker occupies a slot: the moment it finishes, the next queued job starts,
    /// and a job needing the same media file as a running one waits only for that file.
    /// </summary>
    /// <summary>
    /// The sync pump's loop: one pass at a time, and a pass that throws does not end the pump (B31).
    /// </summary>
    /// <remarks>
    /// A count of failed passes is kept, and the pause between them grows, so a body that fails on every pass
    /// costs the server a little rather than all of it.
    /// </remarks>
    /// <param name="keepGoing">Asked before each pass; false ends the loop.</param>
    /// <param name="body">One pass.</param>
    /// <param name="onFault">Called with whatever a pass threw.</param>
    /// <param name="maxIterations">Stop after this many passes (used by the checks; the pump passes int.MaxValue).</param>
    /// <returns>A task that ends when the loop does.</returns>
    internal static async Task RunPumpLoopAsync(
        Func<bool> keepGoing,
        Func<Task> body,
        Action<Exception> onFault,
        int maxIterations = int.MaxValue)
    {
        var consecutiveFaults = 0;
        var iterations = 0;
        while (keepGoing() && iterations < maxIterations)
        {
            iterations++;
            try
            {
                await body().ConfigureAwait(false);
                consecutiveFaults = 0;
            }
            catch (Exception ex)
            {
                // Deliberately everything: the pump dispatches every queued job, so an exception here that ended the
                // loop would leave the queue running with nobody to start it - the same silence B6 fixed for a job
                // stuck in Running. It is logged with the fault that actually happened and the loop carries on after
                // a growing pause, so a body that fails immediately cannot spin.
                consecutiveFaults++;
                onFault(ex);
                await Task.Delay(PumpFaultBackoffMs(consecutiveFaults)).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// How long the pump waits after a failed pass, doubling with consecutive failures up to eight seconds (B31).
    /// </summary>
    /// <param name="consecutiveFaults">How many passes have failed in a row.</param>
    /// <returns>The pause in milliseconds.</returns>
    internal static int PumpFaultBackoffMs(int consecutiveFaults)
        => Math.Min(250 * (1 << Math.Clamp(consecutiveFaults - 1, 0, 5)), 8000);

    private async Task PumpAsync()
    {
        var inFlight = new Dictionary<Task, SyncJob>();
        await RunPumpLoopAsync(
            () => !_disposing,
            () => PumpOnceAsync(inFlight),
            NotePumpFault).ConfigureAwait(false);
    }

    /// <summary>
    /// Records and reports a pass that threw (B31). Internal so the loop's fault path can be driven by name.
    /// </summary>
    /// <remarks>
    /// Nothing here may throw. This runs inside the pump's own fault path, so an exception escaping it would end the
    /// very loop it exists to keep alive - and both things it writes to can fail: the plugin log writes to a file on
    /// a media share that may have gone away, and a service built without a logger has none at all. The count is
    /// incremented first, so even a report that cannot be written leaves the fault visible in the interface.
    /// </remarks>
    /// <param name="exception">What the pass threw.</param>
    internal void NotePumpFault(Exception exception)
    {
        _pumpFaults++;
        var line = $"pump: pass failed ({ExceptionDiagnostics.Describe(exception)}), fault #{_pumpFaults}; "
            + "the queue keeps running";
        try
        {
            PluginLog.Error(line);
        }
        catch (Exception logFailure)
        {
            // No plugin log to write to: the Jellyfin log below is the remaining chance to say what happened.
            _ = logFailure;
        }

        try
        {
            _logger?.LogError(
                ExceptionDiagnostics.RootCause(exception),
                "The sync pump's pass failed for the {Count}(th) time; the pump keeps running",
                _pumpFaults);
        }
        catch (Exception)
        {
            // Same reasoning: a report that cannot be written must not take the pump down with it.
        }
    }

    /// <summary>
    /// Runs one pass of the queue: settle what finished, start what fits, then park until woken (B31).
    /// </summary>
    /// <param name="inFlight">Jobs whose run is under way, keyed by their task.</param>
    /// <remarks>
    /// This is the pump's body, separated from the loop so the loop can guard it. Everything it needs that used
    /// to be a local of the loop is passed in; a pass that decides there is nothing to do returns instead of
    /// continuing, which is the same thing from the loop's point of view.
    /// </remarks>
    private async Task PumpOnceAsync(Dictionary<Task, SyncJob> inFlight)
    {
                List<SyncJob> toStart;
                var limit = 1;
                var running = 0;
                List<(string? VideoPath, bool StillNeeded)> finishedVideos;

                LogWorkerLimit();
                MaybeLogProgress();
                MaybePersistBatchHistory();

                // A job that has stopped making progress is stopped here, so a run cannot hang on it (B6). It
                // runs on the scheduler's own thread and is throttled inside, so this is a cheap call per pass.
                ReapStuckJobs();

                // Recovery for the stores a run leaves behind (B12), throttled to a directory walk every few
                // minutes - and, like the watchdog, run from the cleanup timer as well as from here.
                SweepLongLivedStores();

                // The lock now covers bookkeeping and a snapshot, and nothing else.
                //
                // Planning inside it is what the enqueue path waits on: the enqueue takes this same lock to add its
                // job to the run order (and WakeExtractor takes it again), so every queued item waited for whatever
                // the pump was doing. Measured with `--scenario s40-enqueue` - 56 tasks, the field's shape - the
                // worst enqueue spent every one of its 49 ms in this lock, while the plugin-log write and the pump
                // wake cost nothing at all (S40). On a loaded share that wait is the field's 8-21 s per queued item.
                // Everything the plan reads is therefore snapshotted here (the pump is the only writer of inFlight
                // and the only dispatcher, so the snapshot cannot go stale underneath it) and the plan runs after.
                List<SyncJob> queuedSnapshot;
                List<SyncJob> runningSnapshot;
                List<(SyncJob Job, string? VideoPath)> liveJobs;
                var snapshotHold = System.Diagnostics.Stopwatch.StartNew();
                lock (_queueLock)
                {
                    finishedVideos = new List<(string?, bool)>();

                    foreach (var finished in inFlight.Where(kvp => kvp.Key.IsCompleted).Select(kvp => kvp.Key).ToList())
                    {
                        var finishedJob = inFlight[finished];
                        inFlight.Remove(finished);
                        finishedVideos.Add((
                            _jobContexts.TryGetValue(finishedJob.Id, out var finishedContext)
                                ? finishedContext.Video.Path
                                : null,
                            false));
                    }

                    // A reference is only worth keeping while another subtitle of that same media file is still going
                    // to use it - queued *or* already running. Counting only the queued jobs deleted the tree out
                    // from under the running ones: the last queued task of a batch is dispatched while up to
                    // `ParallelWorkers` jobs of that very file sit in ffsubsync reading the reference it just
                    // removed, and the losers of that race either failed ("unable to read reference") or were handed
                    // the whole container to demux instead. Once no job of the file is queued or running, this run's
                    // copy goes: a wrong reference must not be able to poison a later run of the file.
                    liveJobs = _runOrder
                        .Where(j => j.Status == SyncJobStatus.Queued || j.Status == SyncJobStatus.Running)
                        .Select(j => (j, _jobContexts.TryGetValue(j.Id, out var liveContext)
                            ? liveContext.Video.Path
                            : null))
                        .ToList();

                    queuedSnapshot = _runOrder.Where(j => j.Status == SyncJobStatus.Queued).ToList();
                    runningSnapshot = inFlight.Values.ToList();
                    running = inFlight.Count;
                }

                snapshotHold.Stop();
                LogLockHold("pump-snapshot", snapshotHold.ElapsedMilliseconds);
                var planHold = System.Diagnostics.Stopwatch.StartNew();

                // Which finished files still have a job of their own: the same question the old per-job scan asked,
                // answered from one list instead of re-walking the run order for every finished job.
                finishedVideos = finishedVideos
                    .Select(row => (
                        row.Item1,
                        row.Item1 is not null
                        && liveJobs.Any(live => string.Equals(live.VideoPath, row.Item1, StringComparison.Ordinal))))
                    .ToList();

                toStart = new List<SyncJob>();
                var head = queuedSnapshot.FirstOrDefault();
                if (head is not null)
                {
                    var config = Services.SettingsSource.Current();
                    var headMode = ResolveModeForBatch(head);
                    limit = IsParallelMode(headMode)
                        ? NormalizeWorkers(config?.ParallelWorkers ?? DefaultParallelWorkers)
                        : 1;

                    toStart = PlanStart(
                        queuedSnapshot,
                        runningSnapshot,
                        headMode,
                        head.BatchId,
                        limit,
                        job => MediaVolume.Of(_jobContexts.TryGetValue(job.Id, out var volumeContext)
                            ? volumeContext.Video.Path
                            : null),
                        job => JobNeedsHeavyIo(job, headMode),
                        // Only start a job whose subtitle is already out of the file. The old gate ("may this job
                        // share the file with a running one?") still let jobs through once a reference existed, and
                        // each of them then ran its own pass: measured on a real episode, the lane produced six
                        // subtitles in one pass of 448 MB and the six jobs then ran four more passes of ~350 MB
                        // each, 2 GB of reading for nothing. The lane runs extraction; a job that is not ready waits
                        // in the queue instead of duplicating the work. The one exception is a lane that is not
                        // running at all (disposed, or died), where jobs must be able to extract for themselves
                        // rather than never start.
                        // canShareMediaFile: a second job may join a file whose subtitle is already out.
                        job => ExtractionReady(job),
                        // MayStart: the same, plus the escape hatch for a lane that is gone.
                        job => ExtractionReady(job)
                            || (!LaneAlive
                                && DateTime.UtcNow - _lastPassFinishedUtc > TimeSpan.FromSeconds(20)),
                        walkCapOf: job => WalkCapOfPath(_jobContexts.TryGetValue(job.Id, out var capContext)
                            ? capContext.Video.Path
                            : null));
                }

                // The plan runs on this thread without the lock, and is reported for the same reason the critical
                // sections are: it used to be inside them, and this line is what shows it is not any more (S40).
                planHold.Stop();
                LogLockHold("pump-plan-outside-lock", planHold.ElapsedMilliseconds);

                // Filesystem work stays outside the queue lock: releasing a reference may delete a whole
                // directory, and this lock is what the enqueue path waits on.
                foreach (var (finishedVideo, stillNeededForIt) in finishedVideos)
                {
                    ReferenceStore.EndJob(finishedVideo, stillNeededForIt);

                    // The file is done: nothing of this run will need its reference gate again, so it
                    // does not sit in the dictionary until the next restart.
                    if (!stillNeededForIt && finishedVideo is not null)
                    {
                        _referenceGates.TryRemove(finishedVideo, out _);
                    }
                }

                // Every job that is still waiting says *why*, in the interface. "waiting to start" told the user
                // nothing, and on network storage the wait they were looking at was the file's extraction pass
                // (measured: 46-96 s before the first job of a file can start). The planner already knows both
                // reasons, so it hands them to the job's phase.
                foreach (var waiting in _runOrder)
                {
                    if (waiting.Status == SyncJobStatus.Queued)
                    {
                        waiting.Phase = QueuedReason(waiting, running, limit);
                    }
                }

                if (toStart.Count == 0)
                {
                    if (_disposing)
                    {
                        // The loop asks whether to keep going before the next pass, so a pass that finds the service
                        // going away simply returns and the loop ends (B31).
                        return;
                    }

                    // A drained queue is the one point a batch's sweep records are known to be complete, and they
                    // are written in batches. Flushing here costs one write per idle transition, not one per record,
                    // and it means a run that ended - or a server that is simply left alone afterwards - does not
                    // hold its last records in memory until the next batch happens to cross a batch boundary.
                    if (inFlight.Count == 0)
                    {
                        _sweepState.Value.Flush();
                    }

                    // Nothing to start: either the slots are full (a finishing job wakes the pump) or
                    // the queue is empty (a new job wakes it) - or a job became startable without any
                    // event to signal it, which is what the bounded wait is for. An unbounded wait left
                    // the plugin completely idle with a full queue and free workers: measured on a real
                    // server, 46 s and then 83 s of silence with 225 jobs queued and four slots limit,
                    // while the UI showed "Extracting N subtitles in one pass" - the phase of a job that
                    // was only waiting. Re-planning every three quarters of a second costs nothing
                    // (the predicates are memoised) and turns a stall into a no-op pass.
                    await _wakePump.WaitAsync(TimeSpan.FromMilliseconds(750)).ConfigureAwait(false);
                    return;
                }

                _logger.LogInformation(
                    "Dispatching {Count} job(s) — {Running} running, limit {Limit}, {Queued} queued, batch {Batch}",
                    toStart.Count,
                    running,
                    limit,
                    CountQueued(),
                    toStart[0].BatchId ?? "(standalone)");

                // The plugin log records the dispatch decision, including the reason a parallel run can
                // start fewer jobs than the limit: a job waits for another subtitle of the same media file
                // (they share one audio analysis), so a batch of ten tracks over two episodes runs two,
                // not ten. Without this line that looks like parallelism being ignored.
                var queuedNow = CountQueued();
                var headReason = string.Empty;
                if (limit > 1 && running + toStart.Count < limit && queuedNow > toStart.Count)
                {
                    var queuedMediaFiles = _runOrder
                        .Where(j => j.Status == SyncJobStatus.Queued)
                        .Select(j => _jobContexts.TryGetValue(j.Id, out var c) ? c.Video?.Path : null)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    var runningMediaFiles = inFlight.Values
                        .Select(j => _jobContexts.TryGetValue(j.Id, out var c) ? c.Video?.Path : null)
                        .Where(p => !string.IsNullOrEmpty(p))
                        .Distinct(StringComparer.Ordinal)
                        .Count();
                    headReason = $" — starting {toStart.Count} of {limit}: {queuedNow} queued across {queuedMediaFiles} media file(s), {running} running from {runningMediaFiles} file(s) (a second subtitle of a running file waits for its audio analysis)";
                }

                PluginLog.Info(
                    $"dispatch: starting {toStart.Count}, running {running}, limit {limit}, queued {queuedNow}, batch {toStart[0].BatchId ?? "(standalone)"}{headReason}");

                for (var i = 0; i < toStart.Count; i++)
                {
                    var job = toStart[i];
                    lock (_queueLock)
                    {
                        if (job.Status != SyncJobStatus.Queued)
                        {
                            continue;
                        }

                        // Claimed under the lock so the running count is right for the next pass.
                        job.Status = SyncJobStatus.Running;
                        job.StartedAtUtc = DateTime.UtcNow;
                    }

                    var task = Task.Run(() => RunSyncJobWithContext(job));
                    lock (_queueLock)
                    {
                        inFlight[task] = job;
                    }

                    // Freeing a slot must wake the pump immediately, not at the end of a group.
                    _ = task.ContinueWith(
                        _ => WakePump(),
                        CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);

                    // Small stagger between starts in the same dispatch so several workers do not
                    // hit the disk in the same instant.
                    if (i + 1 < toStart.Count)
                    {
                        await Task.Delay(400).ConfigureAwait(false);
                    }
                }
    }

    /// <summary>Counts queued jobs, for log lines.</summary>
    /// <returns>How many jobs are waiting.</returns>
    private int CountQueued()
    {
        lock (_queueLock)
        {
            return _runOrder.Count(j => j.Status == SyncJobStatus.Queued);
        }
    }

}
