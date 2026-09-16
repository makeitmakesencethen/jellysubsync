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
/// Status of a sync job.
/// </summary>
public enum SyncJobStatus
{
    /// <summary>Job is queued / waiting to start.</summary>
    Queued,
    /// <summary>Job is currently running.</summary>
    Running,
    /// <summary>Job completed successfully.</summary>
    Completed,
    /// <summary>Job failed.</summary>
    Failed,
    /// <summary>Job was cancelled before it ran.</summary>
    Cancelled
}

/// <summary>
/// Represents a subtitle stream that can be synced.
/// </summary>
public class SubtitleInfo
{
    /// <summary>Gets or sets the stream index within the media source.</summary>
    public int Index { get; set; }

    /// <summary>Gets or sets the display title (language + title).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the three-letter language code.</summary>
    public string Language { get; set; } = string.Empty;

    /// <summary>Gets or sets whether this subtitle is external (sidecar file).</summary>
    public bool IsExternal { get; set; }

    /// <summary>
    /// Gets or sets whether this track is flagged forced.
    ///
    /// A forced track usually carries only on-screen signs and text for a language that is otherwise
    /// dubbed — a handful of cues over a whole episode. Reported from real use: a Norwegian track that
    /// was synced turned out to be the two-cue forced track while the file also carried a full WebVTT
    /// track in the same language, so the interface has to be able to tell them apart and the
    /// automatic pick must not prefer the forced one.
    /// </summary>
    public bool IsForced { get; set; }

    /// <summary>
    /// Gets or sets why this track cannot be synced, or null when it can (S5).
    /// </summary>
    /// <remarks>
    /// A bitmap track (PGS, VobSub, DVB, XSUB) is listed like any other and carries the reason here, so the interface
    /// can show the track and say why it is not offered. Hiding it made the list disagree with what exists.
    /// </remarks>
    public string? UnsupportedReason { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether this sidecar was written by the plugin itself (S12).
    /// </summary>
    /// <remarks>
    /// Listed, because the queue accepts it, and flagged, so the interface can label a file the plugin produced rather
    /// than presenting it as another subtitle the media arrived with.
    /// </remarks>
    public bool IsPluginOutput { get; set; }

    /// <summary>Gets or sets the path to the external subtitle file (not serialized in API responses).</summary>
    [JsonIgnore]
    public string? ExternalPath { get; set; }

    /// <summary>Gets or sets whether this subtitle has already been synced.</summary>
    public bool HasSyncedVersion { get; set; }
}

/// <summary>
/// Tracks the state of a single sync job.
/// </summary>
public class SyncJob
{
    private double _progress;
    private string _phase = "Preparing";

    /// <summary>Gets or sets the unique job identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets a value indicating whether this job holds its file's audio-analysis gate.</summary>
    public bool HoldsSpeechGate { get; set; }

    /// <summary>
    /// Gets or sets the account whose request created this job, when one did (F4).
    /// </summary>
    /// <remarks>
    /// Null means the plugin queued the work itself - the scheduled library sweep does - and also covers jobs restored
    /// from a history file written before this field existed. The interface shows a job to its owner and to an
    /// administrator, and to nobody else, so a null owner is visible to administrators only: an unknown owner must
    /// not mean "everybody".
    /// </remarks>
    public Guid? OwnerId { get; set; }

    /// <summary>
    /// Returns a copy of this job, for answering a caller that must not see the server's own fields (F4).
    /// </summary>
    /// <remarks>
    /// The tracked job is the server's record of the run: the sweep reads its output path to decide whether a file is
    /// there, the history writes it out, and an administrator reads it. Sanitising the tracked object to answer one
    /// viewer removed the path from the server's own record - so the copy exists to keep a read from being a write.
    /// </remarks>
    /// <returns>A shallow copy that can be edited without touching the tracked job.</returns>
    public SyncJob CopyForViewer() => (SyncJob)MemberwiseClone();

    /// <summary>Gets or sets the Jellyfin item ID.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the subtitle stream index.</summary>
    public int SubtitleIndex { get; set; }

    /// <summary>
    /// Gets or sets the current status.
    /// </summary>
    public SyncJobStatus Status { get; set; } = SyncJobStatus.Queued;

    /// <summary>
    /// Gets or sets a progress value from 0.0 to 1.0.
    /// </summary>
    /// <remarks>
    /// Setting either this or <see cref="Phase"/> counts as activity: those two are what every progress signal
    /// in the plugin writes - the engine's own output, the extraction lanes' progress lines, the phases the job
    /// passes through - so recording it here means no call site can forget to and leave a working job looking
    /// stalled (see <see cref="StuckJobPolicy"/>).
    /// </remarks>
    public double Progress
    {
        get => _progress;
        set
        {
            _progress = value;
            LastActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Gets or sets the current phase label (e.g. "Extracting subtitle", "Syncing", "Replacing").
    /// The frontend displays this to give the user context about what's happening.
    /// </summary>
    public string Phase
    {
        get => _phase;
        set
        {
            _phase = value;
            LastActivityUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// Gets when this job last showed activity: a phase or progress change, which is every signal the plugin's
    /// own work and the processes it runs produce.
    /// </summary>
    [JsonIgnore]
    public DateTime LastActivityUtc { get; private set; } = DateTime.UtcNow;

    /// <summary>
    /// Records that this job did something, for the stall watchdog.
    /// </summary>
    public void MarkActivity() => LastActivityUtc = DateTime.UtcNow;

    /// <summary>Gets or sets the error message if the job failed.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets the output file path after successful sync.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets a short technical summary of what the sync changed
    /// (e.g. "offset −1250 ms", "framerate ratio 1.0004×"), for History.</summary>
    public string? Outcome { get; set; }

    /// <summary>
    /// Gets or sets how the embedded subtitle was obtained and what it cost, e.g. "read through the
    /// container index (matroska-cues), 31 ms" or "demuxed with ffmpeg, 96000 ms". A subtitle that
    /// took two minutes to extract is otherwise indistinguishable from one that took thirty
    /// milliseconds, in the interface and in the history alike.
    /// </summary>
    public string? ExtractionNote { get; set; }

    /// <summary>Gets or sets when the job was created (queue order).</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the job reached a terminal state.</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Gets or sets when the job actually started running (for elapsed-time display).</summary>
    public DateTime? StartedAtUtc { get; set; }

    /// <summary>Gets or sets the batch this job belongs to (null for standalone jobs).</summary>
    public string? BatchId { get; set; }

    /// <summary>Gets or sets the 0-based position of this job inside its batch.</summary>
    public int BatchIndex { get; set; } = -1;

    /// <summary>Gets or sets the batch scope label (e.g. "Series · Season 2").</summary>
    public string? BatchLabel { get; set; }

    /// <summary>Gets or sets the human display label (subtitle/track title).</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the multi-subtitle mode this job runs in
    /// (normal | parallel | fast).</summary>
    public string Mode { get; set; } = "normal";
}

/// <summary>
/// One entry of a bulk subtitle listing: a movie or an episode, with its syncable tracks.
/// </summary>
public class BulkSubtitleItem
{
    /// <summary>Gets or sets the item (movie or episode) id.</summary>
    public Guid Id { get; set; }

    /// <summary>Gets or sets the series id when this entry came from expanding one.</summary>
    public Guid? SeriesId { get; set; }

    /// <summary>Gets or sets the display name.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the syncable subtitle tracks.</summary>
    public List<SubtitleInfo> Tracks { get; set; } = new();
}

/// <summary>
/// Describes the installation status of the managed ffsubsync.
/// </summary>
public class FfSubSyncInstallationStatus
{
    /// <summary>Gets or sets whether ffsubsync is ready to use.</summary>
    public bool IsInstalled { get; set; }

    /// <summary>Gets or sets the path to the managed ffsubsync binary, if installed.</summary>
    public string? ManagedBinaryPath { get; set; }

    /// <summary>Gets or sets the path to the managed virtualenv.</summary>
    public string? VenvPath { get; set; }

    /// <summary>Gets or sets the resolved binary path that will be used (managed or custom).</summary>
    public string? ResolvedBinaryPath { get; set; }

    /// <summary>
    /// Gets or sets the explanation shown when the engine that would run could not be found (D2).
    /// </summary>
    /// <remarks>
    /// Null when the engine is there. A user whose configured path is wrong needs to be told which path the plugin
    /// looked at, or the badge just says "not installed" while the installer they are not being offered is irrelevant.
    /// </remarks>
    public string? EngineNote { get; set; }

    /// <summary>Gets or sets whether a system python3 was found.</summary>
    public bool PythonAvailable { get; set; }

    /// <summary>Gets or sets the python3 version string, if found.</summary>
    public string? PythonVersion { get; set; }

    /// <summary>Gets or sets the ffsubsync version string, if installed.</summary>
    public string? FfSubSyncVersion { get; set; }

    /// <summary>Gets or sets a summary of the cached speech analysis used by fast mode.</summary>
    public string? SpeechCacheSummary { get; set; }

    /// <summary>
    /// Gets or sets a summary of the extracted-subtitle cache (F16).
    /// </summary>
    /// <remarks>
    /// The audio cache was described in the interface and this one was not, although it is the larger of the two on a
    /// library that has been synced for a while: it holds the subtitle text taken out of each media file, on disk for
    /// 120 days and capped in size, plus a bounded in-memory layer for the files being worked on right now.
    /// </remarks>
    public string? SubtitleCacheSummary { get; set; }

    /// <summary>Gets or sets the bundled ffsubsync version (plugin-shipped binary), if present.</summary>
    public string? BundledFfSubSyncVersion { get; set; }

    /// <summary>
    /// Gets or sets the running plugin version. Reported with the status so the interface can show
    /// which build it is talking to without a second request, and so the build number does not have
    /// to be crammed into the progress line.
    /// </summary>
    public string PluginVersion { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets where the plugin's own log file lives (path, size, rotated count), so it can be
    /// found from the interface instead of guessed at from inside a container.
    /// </summary>
    public string LogFile { get; set; } = string.Empty;

    /// <summary>Gets or sets the runtime identifier the bundled binary was built for, if present.</summary>
    public string? BundledRid { get; set; }

    /// <summary>
    /// Gets or sets where the plugin was loaded from and which settings file it reads — the two
    /// things that explain a setting which appears to be ignored (a second loaded copy keeps its
    /// own configuration).
    /// </summary>
    public string? PluginIdentity { get; set; }

    /// <summary>
    /// Gets or sets the worker count in force against the value configured, e.g.
    /// <c>4 in use (setting 8)</c>.
    /// </summary>
    public string? WorkerSummary { get; set; }
}

/// <summary>
/// Describes the result of one library sweep run.
/// </summary>
public class SweepResult
{
    /// <summary>Gets or sets how many video items were scanned.</summary>
    public int ScannedItems { get; set; }

    /// <summary>Gets or sets how many subtitle tracks were queued for sync.</summary>
    public int CandidatesEnqueued { get; set; }

    /// <summary>Gets or sets how many tracks were skipped because their synced output already exists.</summary>
    public int SkippedCached { get; set; }

    /// <summary>Gets or sets how many tracks were skipped due to repeated failures.</summary>
    public int SkippedFailed { get; set; }

    /// <summary>Gets or sets how many tracks were skipped for another reason (e.g. synced this session).</summary>
    public int SkippedOther { get; set; }

    /// <summary>Gets or sets how many queued tracks finished successfully.</summary>
    public int Completed { get; set; }

    /// <summary>Gets or sets how many queued tracks failed or were cancelled.</summary>
    public int FailedOrCancelled { get; set; }
}

/// <summary>
/// Service that manages ffsubsync installation and runs sync jobs.
/// </summary>
public partial class SubSyncService : IDisposable
{
    private readonly ILogger<SubSyncService> _logger;
    /// <summary>Gets the engine resolver, the bundled-binary cache and the installer (C9 of the split).</summary>
    private readonly FfSubSyncEngine _engine;

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryMonitor _libraryMonitor;

    // Lets the item refresh run once per item instead of once per subtitle track; the folder report
    // that makes Jellyfin discover the file is never suppressed.
    private readonly LibraryRefreshGate _refreshGate = new();
    private readonly ConcurrentDictionary<string, SyncJob> _jobs = new();


    // Set on Dispose to stop the background queue pump
    private bool _disposing;

    // Single global FIFO queue: every sync (detail-page or batch) is a job in
    // this queue; one background pump runs them strictly one at a time, so
    // overlapping batches can never race on the same subtitle files.
    private readonly object _queueLock = new();


    private readonly List<SyncJob> _runOrder = new();
    private Task? _pumpTask;

    /// <summary>Gets how many pump passes have failed since start-up (B31), so a repeated fault is visible.</summary>
    internal int PumpFaults => _pumpFaults;

    private int _pumpFaults;
    // Unbounded on purpose. With a bounded semaphore, several wakes collapse into one and the pump
    // can consume the signal before the job that needed it finishes, so the next completion had to
    // wait for something else to wake it - dispatch lines 19 seconds apart while eight slots sat
    // idle. Extra signals only cost an empty pass or two.
    private readonly SemaphoreSlim _wakePump = new(0);

    // --- the extraction lane ------------------------------------------------------------------
    // Reading a media file is the slow part and it is not what a sync worker should be doing: a worker
    // that reads a file occupies a slot for tens of seconds while ffsubsync - about a second of work -
    // waits behind it, and every other subtitle of that file waits for the same read. So extraction
    // happens in its own lane, and a job is only started once its subtitle is already out of the file
    // (see ExtractionReady). A pass that has produced 1 of 30 languages lets that one job start
    // immediately instead of holding all thirty.
    private readonly SemaphoreSlim _extractWake = new(0);
    // Value = when this pass started, so the progress line can say how long the lane has been on that
    // file (S24). Nothing else reads the value; the keys are the in-flight set.
    private readonly ConcurrentDictionary<string, DateTime> _passInFlight = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _extractedReady = new(StringComparer.Ordinal);

    /// <summary>
    /// Extraction lanes in flight. One file per lane: a batch of episodes is otherwise eight passes
    /// one after another (measured: S01E06 31 s, S01E07 20 s, S01E08 18 s), while the whole point of
    /// the lane is to keep the sync workers fed. Derived from the worker limit so it scales with the
    /// machine instead of with one particular NAS.
    /// </summary>
    private readonly List<Task> _laneTasks = new();
    private readonly ConcurrentDictionary<string, List<int>> _referenceOrdinals = new(StringComparer.Ordinal);

    /// <summary>
    /// Tracks a pass has already tried and come back empty for (a bitmap track with no text). Without
    /// this the lane would ask for the same track twice a second, forever, on the user's storage.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _extractTried = new(StringComparer.Ordinal);

    /// <summary>When the lane last finished a pass, used to tell a lane that is gone from one that is busy.</summary>
    private DateTime _lastPassFinishedUtc = DateTime.UtcNow;

    /// <summary>When the stall watchdog last looked at the running jobs; ticks, so both callers can read it.</summary>
    private long _lastReapTicks;

    /// <summary>
    /// How often the stall watchdog looks, in seconds. The pump runs more often than this; the throttle keeps
    /// a batch of running jobs from being walked on every pass.
    /// </summary>
    private const int ReapIntervalSeconds = 20;

    /// <summary>When the long-lived stores were last swept; ticks, so both callers can read it.</summary>
    private long _lastStoreSweepTicks;

    /// <summary>
    /// How often the stores that outlive a job are swept, in minutes. Slower than the watchdog on purpose:
    /// this one walks the cache directory, and none of what it removes costs anything while it waits (B12).
    /// </summary>
    private const int StoreSweepMinutes = 5;

    // Subtitle text extracted from a file while it was being read for another subtitle of the same
    // file. Each entry is a few kilobytes of text; the queue keeps the oldest ones out.
    private readonly ConcurrentDictionary<string, string> _extractedText = new(StringComparer.Ordinal);
    private readonly ConcurrentQueue<string> _extractedOrder = new();
    private const int ExtractedCacheLimit = 256;

    // One gate per media file: only one job builds that file's reference subtitle, the others wait
    // for it and then reuse the file. Two builders raced on a single shared "<target>.part" and the
    // loser either failed to write it or failed to move it into place, which used to end in the
    // engine being handed the whole container to demux.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _referenceGates = new(StringComparer.Ordinal);

    // One gate per media file for the *audio* analysis, which is the expensive half of a job that has no
    // subtitle to align against: measured on a real server, two subtitle tracks of one 2 h movie were
    // started together and each ran the engine against the audio - 141 s each, twice, and both reported
    // cachedSpeech=False because neither had harvested the result yet. The first job through the gate
    // analyses; the others wait and then reuse what it stored in the speech cache.
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _speechGates = new(StringComparer.Ordinal);

    // A lane pass reads a whole file in one go, tens of seconds on local storage and minutes on a
    // slow share. Kill cancels the jobs, the engine's process trees and this source; it is replaced
    // immediately afterwards so the next pass starts from a fresh one (a token that stays cancelled
    // would abort every later extraction as soon as it started).
    private volatile CancellationTokenSource _laneStop = new();
    private readonly ConcurrentDictionary<string, (Video Video, MediaBrowser.Model.Entities.MediaStream Stream, int Ordinal, Configuration.PluginConfiguration Config)> _jobContexts = new();

    // Per-job cancellation. Cancelling a batch only drops queued work; killing running
    // work means terminating the ffsubsync/ffmpeg processes, which is what the UI's
    // "Kill" action does.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _jobCancellation = new();

    /// <summary>
    /// Every child process this service runs (ffsubsync, ffmpeg, python3, pip), so Kill can terminate the
    /// whole tree instead of only cancelling a token and hoping the process notices.
    /// </summary>
    private readonly SubSyncProcesses _processes;

    // Cleanup timer for evicting old completed/failed jobs
    private readonly Timer _cleanupTimer;

    // Persistent skip/fail cache for library sweeps (external subtitle files only)
    private readonly Lazy<SweepState> _sweepState = new(() =>
        new SweepState(Path.Combine(Plugin.Instance?.StatePath ?? Path.GetTempPath(), "sweep-cache.json")));

    /// <summary>Allowed values for the --vad config option (subset of ffsubsync's engine choices).</summary>
    /// <summary>
    /// The VAD the engine is given whenever the *plugin* intends the audio to be the reference.
    /// </summary>
    /// <remarks>
    /// `webrtc`, not the configured method, and not because it is better: because it is the only one that reads
    /// audio. Measured 2026-09-15 (S43): with the default `subs_then_webrtc` the engine takes the video's own
    /// **embedded subtitle tracks** as its speech signal, and the reference the plugin hands it for "the audio" is
    /// the video - so on a file with subtitles the audio path is a subtitle path, chosen by the engine, bypassing
    /// the plugin's own reference checks (cue count, signs-track detection, span). It read a wrong-cut track in
    /// one fixture (+24,170 s where the film's audio says -5,080 s) and scored a subtitle-derived signal 212 234
    /// against the audio's 53 566 on another. Where the plugin has supplied a subtitle reference it has already
    /// vetted, the configured method stands.
    /// </remarks>
    private const string AudioReferenceVad = "webrtc";

    internal static readonly HashSet<string> AllowedVadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "subs", "webrtc", "subs_then_webrtc", "auditok", "subs_then_auditok", "subs_then_silero", "silero"
    };

    /// <summary>Allowed values for the --output-encoding config option.</summary>
    internal static readonly HashSet<string> AllowedOutputEncodings = new(StringComparer.OrdinalIgnoreCase)
    {
        "utf-8", "ascii", "latin-1", "utf-8-sig", "utf-16"
    };


    /// <summary>Gets the resolved ffsubsync binary a job will execute (see <see cref="FfSubSyncEngine"/>).</summary>
    /// <returns>The engine path.</returns>
    public string ResolveFfSubSyncPath() => _engine.ResolveFfSubSyncPath();

    /// <summary>Gets the ffmpeg binary the extraction and the container probe run.</summary>
    /// <returns>The ffmpeg path.</returns>
    private string ResolveFfmpegPath() => _engine.ResolveFfmpegPath();

    /// <summary>Gets the identity string the audio-analysis cache is keyed on.</summary>
    /// <returns>The engine identity.</returns>
    private string EngineIdentity() => _engine.EngineIdentity();

    /// <summary>Gets how many times the bundled engine was spawned to ask its version (B5).</summary>
    internal long EngineVersionProbes => _engine.EngineVersionProbes;

    /// <summary>Says whether the engine that would actually run can be found (D2).</summary>
    /// <returns>True when the resolved engine exists.</returns>
    public bool EngineIsInstalled() => _engine.EngineIsInstalled();

    /// <summary>Checks the installation status of ffsubsync.</summary>
    /// <returns>Detailed installation status, with the worker count in force beside the configured one.</returns>
    public Task<FfSubSyncInstallationStatus> GetInstallationStatusAsync()
        => _engine.GetInstallationStatusAsync($"{EffectiveWorkerLimit} in use (setting {ConfiguredWorkerLimit})");

    /// <summary>Installs ffsubsync into the managed virtualenv.</summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the install has finished.</returns>
    public Task InstallFfSubSyncAsync(CancellationToken cancellationToken)
        => _engine.InstallFfSubSyncAsync(cancellationToken);

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="libraryMonitor">Jellyfin library filesystem monitor (for targeted folder rescans).</param>
    /// <param name="userManager">Jellyfin's user manager, for the per-item access check.</param>
    public SubSyncService(ILogger<SubSyncService> logger, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor, IUserManager userManager)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _userManager = userManager;
        _libraryMonitor = libraryMonitor;

        // B5: the probe goes through the plugin's own process runner, so the shipped path is the one the checks
        // exercise - and it reports the one spawn per binary, which is what the rig counts from outside.
        _processes = new SubSyncProcesses(logger);
        _engine = new FfSubSyncEngine(logger, _processes);


        // Evict completed/failed jobs older than 1 hour, check every 30 minutes
        _cleanupTimer = new Timer(_ => CleanupOldJobs(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

        RestoreBatchHistory();

        // A plugin that was stopped while jobs were running leaves their scratch directories behind, and nothing else
        // removes them until someone clears the cache by hand (F7). This runs before the first pass, so the cache root
        // starts clean rather than after the first maintenance window.
        try
        {
            var orphans = ClearStaleJobDirectories();
            if (orphans > 0)
            {
                PluginLog.Info($"startup: removed {orphans} orphaned job scratch director(ies)");
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not sweep orphaned scratch directories at startup");
        }
    }

    // S25: the batch history is persisted so a restart no longer empties the History tab. Jobs restored
    // from disk are display rows only - a job with no _jobContexts entry can never be started by the
    // pump - and are marked here so the cleanup timer never evicts the history as if it were live work.
    private readonly HashSet<string> _historyOnlyJobs = new(StringComparer.Ordinal);
    private string _historySignature = string.Empty;
    private DateTime _historySavedUtc = DateTime.MinValue;

    // S24: the periodic progress line. The only depth figure the plugin had was the dispatch line, which
    // appears only when a job starts and names only the batch that job belongs to - so "how much is left"
    // could not be answered from the log without hand-parsing lane lines and dispatch timestamps (which is
    // exactly what a 2 497-task run needed).
    private const int ProgressLineSeconds = 60;
    private DateTime _lastProgressLineUtc = DateTime.MinValue;

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
    /// True when a job will read a lot from storage: it must extract an embedded subtitle,
    /// or run a speech analysis whose result is not cached yet.
    /// </summary>
    private bool JobNeedsHeavyIo(SyncJob job, string mode)
    {
        if (!_jobContexts.TryGetValue(job.Id, out var ctx))
        {
            return false;
        }

        if (!ctx.Stream.IsExternal)
        {
            return true; // embedded extraction reads the container
        }

        return SyncJobMode.UsesSpeechCache(mode) && !SpeechIsCached(job);
    }

    /// <summary>
    /// True when this job's media file already has its speech analysis cached, so it can run
    /// without touching storage (and may share the file with another worker).
    /// </summary>
    /// <summary>
    /// Deletes the scratch directory of every job the plugin is no longer tracking. Used by the
    /// "clear cache" action: a job's directory holds its extracted subtitle and the engine's output, so
    /// only directories whose job is gone (or never existed - a crash) are removed, never a running one.
    /// </summary>
    /// <returns>Number of directories removed.</returns>
    public int ClearStaleJobDirectories()
    {
        var root = Plugin.Instance?.TempPath;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return 0;
        }

        var removed = 0;
        var rootFull = Path.GetFullPath(root);
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);

            // Only a directory that is named after a job - the shape this plugin creates for a job's scratch
            // space - may be deleted (B23). Anything else in the cache directory (the reference tree, the
            // shared extraction directories, a log or state directory, or a directory somebody else put
            // there) is not this method's to remove, and a recursive delete of a misconfigured root used to
            // take files outside the plugin's own scratch with it.
            if (!SyncedTargetNaming.IsJobScratchDirectory(name))
            {
                continue;
            }

            // A job that is still queued or running may be using its directory right now, so it is left alone. A job
            // that is merely *known* - it finished, failed, was cancelled, or was restored from the history file after
            // a restart - does not protect its directory: that is precisely the directory this sweep is for (F7). The
            // first version of this check asked only whether the id was known, which left every interrupted job's
            // directory in place forever once its record had been written to the history file.
            if (_jobs.TryGetValue(name, out var tracked)
                && tracked.Status is SyncJobStatus.Queued or SyncJobStatus.Running)
            {
                continue;
            }

            // Belt and braces: the recursive delete only ever runs on a path that resolved inside the root.
            if (!SyncedTargetNaming.IsInsideRoot(rootFull, directory))
            {
                PluginLog.Warn($"clear scratch: refused to delete {directory} (outside {rootFull})");
                continue;
            }

            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch (IOException)
            {
                // A job may have just started using it; the next clear picks it up.
            }
            catch (UnauthorizedAccessException)
            {
                // Same.
            }
        }

        // The shared extraction directories are not jobs' business, so they are not counted above: they go
        // when no job that reads them is left (see SharedExtractionStore).
        removed += SharedExtractionStore.Cleanup(id => _jobs.ContainsKey(id));

        return removed;
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
    /// Releases a job's hold on its file's audio analysis, if it has one.
    /// </summary>
    /// <param name="job">The job that may be holding it.</param>
    /// <param name="videoPath">The media file.</param>
    private void ReleaseSpeechGate(SyncJob job, string videoPath)
    {
        if (!job.HoldsSpeechGate)
        {
            return;
        }

        job.HoldsSpeechGate = false;
        if (_speechGates.TryGetValue(videoPath, out var gate))
        {
            // Released only by the job that took it (the flag above), and never twice: the harvest path and
            // the job's finally both come through here.
            gate.Release();
        }
    }

    /// <summary>Joins a note with another, so callers do not repeat the separator.</summary>
    /// <param name="existing">Note so far, or null.</param>
    /// <param name="addition">Note to append.</param>
    /// <returns>The combined note.</returns>
    private static string Join(string? existing, string addition) =>
        string.IsNullOrEmpty(existing) ? addition : existing + " \u00b7 " + addition;

    /// <summary>How long one answer about a media file's speech cache is trusted.</summary>
    private static readonly TimeSpan SpeechCachedTtl = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, (bool Cached, DateTime At)> _speechCachedMemo = new(StringComparer.Ordinal);

    private readonly object _speechCachedGate = new();

    /// <summary>How long "the extraction cache does not have this track yet" is trusted (S7).</summary>
    /// <remarks>
    /// The answer costs a read of the extraction cache, and the scheduler asks for it for every queued job on
    /// every planning pass - so a queue waiting on a lane re-probed the same directory once per job per pass, while
    /// that directory's volume was busy being read. A remembered miss cannot hide a track that has arrived: the
    /// hand-over adds the key to <c>_extractedReady</c> first, and that is checked before this.
    /// </remarks>
    private static readonly TimeSpan ExtractedMissTtl = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, DateTime> _extractedMissMemo = new(StringComparer.Ordinal);

    private readonly object _extractedMissGate = new();

    /// <summary>Most extraction lanes to run at once.</summary>
    /// <summary>
    /// Enqueue duration above which one line is written with its phase breakdown.
    /// </summary>
    /// <remarks>
    /// 250 ms in the field, where anything slower than that is worth naming. The measurement S40 needs cannot
    /// wait for a slow share: a rig runs on local storage and would never reach 250 ms, so the threshold is
    /// settable (<c>SUBSYNC_ENQUEUE_TRACE_MS</c>) and the rig sets it low to see where the time goes.
    /// </remarks>
    private static readonly long EnqueueTraceMs = ResolveEnqueueTraceMs();

    /// <summary>
    /// Writes one line when a critical section on the queue lock was held long enough to be another caller's wait.
    /// </summary>
    /// <remarks>
    /// The enqueue's own breakdown reports how long it waited for this lock (S40). A wait says nothing about who
    /// held it, and the holders are several: the pump's snapshot, the dispatch claim, the extraction lane's scan for
    /// the next file, and the two cancel paths. Each reports its own hold time above the same threshold, so a field
    /// run names the holder instead of leaving "the enqueue is slow" as the finding.
    /// </remarks>
    /// <param name="holder">Short name of the critical section.</param>
    /// <param name="milliseconds">How long it was held.</param>
    private static void LogLockHold(string holder, long milliseconds)
    {
        if (milliseconds > EnqueueTraceMs)
        {
            PluginLog.Info($"queue lock slow: holder={holder} ms={milliseconds}");
        }
    }

    /// <summary>Reads the enqueue trace threshold, defaulting to the field's 250 ms.</summary>
    /// <returns>The threshold in milliseconds.</returns>
    private static long ResolveEnqueueTraceMs()
    {
        var raw = Environment.GetEnvironmentVariable("SUBSYNC_ENQUEUE_TRACE_MS");
        return long.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0
            ? parsed
            : 250;
    }

    private const int MaxExtractionLanes = 3;

    /// <summary>
    /// How many stderr lines of an extraction are kept for judging it. ffmpeg reports a truncation when it
    /// reaches the end of the input, so the tail holds it; the bound keeps a long demux's progress lines from
    /// growing the job's memory.
    /// </summary>
    private const int ExtractionStderrTailLines = 200;

    /// <summary>Reference hint used for the cache key when only the job is known.</summary>
    private string SpeechCacheReferenceHint(SyncJob job) =>
        _jobContexts.TryGetValue(job.Id, out var ctx) && ctx.Stream.IsExternal ? "auto" : "embedded";

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
    /// Describes what ffsubsync is about to do, in the words of what it actually does.
    ///
    /// Three distinct situations, and conflating them is what made a 0.6 s subtitle comparison
    /// look like a fresh audio analysis on every subtitle of the same file:
    /// a stored analysis is reused, the audio is analysed, or another subtitle track of the file
    /// is used as the reference.
    /// </summary>
    /// <param name="fromCache">Whether the stored speech analysis is being reused.</param>
    /// <param name="audioReference">Whether the reference is the file's audio.</param>
    /// <returns>Phase text for the job.</returns>
    public static string SyncPhaseLabel(bool fromCache, bool audioReference)
    {
        if (fromCache)
        {
            return "Syncing (from cache)";
        }

        return audioReference
            ? "Syncing (analysing the audio)"
            : "Syncing (using another subtitle track)";
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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _ceilingLogged =
        new(StringComparer.Ordinal);

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

    private static string NormalizeMode(string? mode) => SyncJobMode.Normalize(mode);

    private static bool IsParallelMode(string mode) => SyncJobMode.IsParallel(mode);

    private static bool IsSpeechCachingMode(string mode) => SyncJobMode.UsesSpeechCache(mode);

    /// <summary>
    /// Reads per call above which a volume is treated as storage-bound rather than fast - **the fallback
    /// only**, used when this process has no second volume to measure against (see `WalkCapForProfile`). A wide
    /// margin
    /// above the class default a read policy starts from (0,05 ms per call) and far below fabji's share,
    /// which measures 13-46 ms per call at rest.
    /// </summary>
    public const double SlowReadMsPerCall = 5.0;

    /// <summary>
    /// Reads per call above which even two concurrent walks are too many for a volume - the bottom of what
    /// that share shows when it is thrashing (1419-1613 ms per call observed while eight walks ran).
    /// </summary>
    public const double ThrashingReadMsPerCall = 100.0;

    /// <summary>
    /// The ceiling a volume carries until something has measured it, so that a wave planned before the first
    /// read cannot walk a storage-bound volume eight abreast. A volume that measures fast is uncapped from
    /// its first measured pass onwards.
    /// </summary>
    public const int UnmeasuredWalkCap = 2;

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
    /// Throughput above which a volume's own walk says its storage is not the constraint, in MB/s - **the
    /// fallback only**, used when nothing else on this machine has enough samples to be the reference.
    /// </summary>
    /// <remarks>
    /// 50, set between the two populations the field has shown, with margin on both sides. On 2026-09-14 the
    /// share's walks measured 16,8 / 17,1 / 17,4 / 18,9 / 19,7 / 22,7 / 22,7 / 23,1 MB/s over eight episodes,
    /// and local NVMe measured 84,4 / 85,3 / 85,6 / 91,0 / 91,4 MB/s over five, with a lone 2,4 GB file at
    /// 137 MB/s. That is a 2,2x margin above every walk this share has produced and 1,7x below every local
    /// walk. The first version of this threshold was 20 and the field run caught it: 20 sits *inside* the
    /// share's own range, so the ceiling lifted on a 23,1 MB/s walk and dropped on a 17,4 MB/s one, and the
    /// wave planned while it read "fast" put **five concurrent walks** on the share. It was derived from a
    /// 2,6 MB/s figure measured while that volume was already being walked eight at a time - a throttled
    /// measurement cannot set the boundary that decides whether to throttle.
    /// </remarks>
    public const double FastWalkMbPerSec = 50.0;

    /// <summary>
    /// Throughput below which a volume's own walk says its storage is thrashing, in MB/s: nothing measured
    /// has come near this, so it is a floor rather than a threshold observed in the field.
    /// </summary>
    public const double ThrashingWalkMbPerSec = 1.0;

    /// <summary>How much a volume is read to give it its first measurement, when it has none.</summary>
    private const int ProbeBytes = 16 * 1024;

    /// <summary>
    /// Reads the first measurement of a volume takes. See <see cref="ThrashTierMinReads"/>: one read cannot
    /// tell a volume's steady state, and the field's single cold read held a share to one walk for a run.
    /// </summary>
    private const int ProbeReads = 3;

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
    /// How much of the configured reference ceiling a demanded shift may reach before the answer is cross-checked
    /// against the film's own audio.
    /// </summary>
    /// <remarks>
    /// A third of <c>MaxSubtitleReferenceOffsetSeconds</c>: 10 s at the 30 s default, which is the number this
    /// band used to be written as in the log note ("a shift this size usually means that track is not the same
    /// cut"). Deriving it keeps the default behaviour identical while making it follow the setting the user
    /// actually controls.
    /// </remarks>
    public const double SuspiciousReferenceShiftFraction = 1.0 / 3.0;

    /// <summary>The ceiling a storage-bound volume is held to: two walks at a time.</summary>
    public const int StorageBoundWalkCap = 2;

    /// <summary>
    /// How far below the best walk this machine has measured a volume may fall before it is storage-bound.
    /// </summary>
    /// <remarks>
    /// A ratio rather than a throughput, so the ceiling needs to know nothing about the hardware. It is also
    /// right for fast volumes, which is the part that looks wrong at first: a share at a third of the machine's
    /// best is still one whose link eight walks would saturate, and holding it to two leaves the aggregate where
    /// it was while each file finishes sooner. On the machine this was written for the ratio reads 0,04x for the
    /// share and 1,0x for the local disk, reproducing exactly what absolute thresholds used to decide.
    /// </remarks>
    public const double WalkBoundFraction = 0.5;

    /// <summary>
    /// How close to that best a volume must come before a ceiling already in force is released.
    /// </summary>
    /// <remarks>
    /// Higher than <see cref="WalkBoundFraction"/> on purpose: that gap is the hysteresis, and without it a
    /// volume whose walks sit near a single threshold flips between two and no ceiling inside one batch - which
    /// is what the field showed on 2026-09-14, twice, before this existed.
    /// </remarks>
    public const double WalkReleaseFraction = 0.667;

    /// <summary>
    /// How many reads must stand behind a read sample before it may call a volume thrashing.
    /// </summary>
    /// <remarks>
    /// From the field, 2026-09-14 (S41): the first measurement of a volume was a *single* cold read, taken a
    /// third of the way into a file while that share was still loaded. It came back at 231,34 ms against a
    /// steady state of 13-46 ms, and because 231 ms is over the absolute thrash threshold the share was held
    /// to one walk for the whole run - fifteen hold lines over fourteen minutes, all quoting 231,3 ms, while
    /// its own later walks moved 16,8-23,1 MB/s, which is two walks' worth. One read is not a steady state, so
    /// a thrash verdict now needs backing: below this many reads the volume is held at
    /// <see cref="StorageBoundWalkCap"/> and the reason says why. Two is the smallest number that can disagree
    /// with one, which is the whole point of the threshold.
    /// </remarks>
    public const int ThrashTierMinReads = 2;

    /// <summary>How many times the machine's best read latency counts as storage-bound.</summary>
    public const double SlowReadRatio = 20.0;

    /// <summary>Within how many times that latency a volume's reads count as fast.</summary>
    public const double FastReadRatio = 10.0;

    /// <summary>
    /// How many times that latency counts as thrashing, where even two concurrent walks are too many.
    /// </summary>
    /// <remarks>
    /// 400, because the reference is floored at 0,25 ms (see `VolumeProfiles.PageCacheFloorMsPerCall`) and
    /// 400 x 0,25 is the 100 ms per read that used to be the absolute thrash threshold. At 100 this would have
    /// been 25 ms, so a share reading at 13-46 ms - fabji's, on 2026-09-14 - would have been held to one walk at
    /// a time instead of the two its own measurements say are its best. Ratios have to be chosen so the old
    /// behaviour comes out of them, or "portable" quietly means "different".
    /// </remarks>
    public const double ThrashingReadRatio = 400.0;

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

    /// <summary>
    /// Upper bound for <see cref="Configuration.PluginConfiguration.ParallelWorkers"/>.
    ///
    /// This is not a performance opinion: it exists so a mistyped value (10000) cannot spawn
    /// thousands of processes. Everything from 1 upwards is honoured as written — 1, 3, 32 all
    /// mean exactly what they say, and the setting is the only thing that decides the width.
    /// </summary>
    public const int MaxParallelWorkers = 64;

    /// <summary>Worker count for parallel mode, clamped to the documented range.</summary>
    /// <param name="workers">Configured worker count.</param>
    /// <returns>The count actually used, which is the configured one for every value in range.</returns>
    public static int NormalizeWorkers(int workers) =>
        workers < 1 ? 1 : (workers > MaxParallelWorkers ? MaxParallelWorkers : workers);

    private static int _lastLoggedWorkerLimit = -1;

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

    /// <summary>Default worker count for parallel mode.</summary>
    public const int DefaultParallelWorkers = 4;

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

    /// <summary>
    /// Runs one job and guarantees that it leaves a terminal state behind (B6).
    /// </summary>
    /// <remarks>
    /// A job set to <c>Running</c> and left there is the whole of B6: it holds a worker slot and the run it
    /// belongs to never reports itself finished, with a restart as the only way out. The paths that produced it
    /// were an exception raised outside the job's own error handling (a media file that vanished, a fault
    /// before the job's <c>try</c> was reached) and any path that returned without settling its status. Neither
    /// can happen quietly any more: every exit passes <see cref="StuckJobPolicy.Settle"/>, which fails a job
    /// that is still running and says so in the plugin log, and a watchdog stops jobs that stop making progress
    /// (see <see cref="ReapStuckJobs"/>).
    /// </remarks>
    /// <param name="job">The job to run.</param>
    private async Task RunSyncJobWithContext(SyncJob job)
    {
        if (!_jobContexts.TryGetValue(job.Id, out var ctx))
        {
            job.Status = SyncJobStatus.Failed;
            job.Error = "Job context was evicted before it started (server restarted?).";
            return;
        }

        using var cts = new CancellationTokenSource();
        _jobCancellation[job.Id] = cts;
        try
        {
            await RunSyncJob(job, ctx.Video, ctx.Stream, ctx.Ordinal, ctx.Config, cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // A job the watchdog already settled keeps the reason it was stopped for: "cancelled by user"
            // would name an action nobody took.
            if (job.Status == SyncJobStatus.Running)
            {
                job.Status = SyncJobStatus.Cancelled;
                job.Phase = "Cancelled";
                job.Error = "Cancelled by user.";
                _logger.LogInformation("Job {JobId} cancelled by user", job.Id);
            }
        }
        catch (Exception ex)
        {
            // Anything the job's own handler did not catch - the failure used to end the task and leave the
            // job Running for good.
            _logger.LogError(ex, "Sync job {JobId} ended with an unhandled error", job.Id);
            PluginLog.Error($"job {job.Id} ended with an unhandled error: {ex.Message}", ex);
            if (job.Status is SyncJobStatus.Running or SyncJobStatus.Queued)
            {
                job.Status = SyncJobStatus.Failed;
                job.Error = ex.Message;
            }
        }
        finally
        {
            _jobCancellation.TryRemove(job.Id, out _);
            JobProcessRegistry.Forget(job.Id);

            // The phase is read before settling: it is the last thing the job reported, and it is the only
            // thing in the log that says where a job that never finished had got to.
            var phaseAtExit = job.Phase;
            if (StuckJobPolicy.Settle(
                    job, DateTime.UtcNow,
                    "the job ended without reaching a terminal state; the plugin log has the last phase it reported"))
            {
                PluginLog.Warn(
                    $"[{job.Id}] job ended while still running (phase '{phaseAtExit}') - marked failed so it "
                    + "cannot hold its worker slot or keep its run looking unfinished");
            }
        }

        // Sweep cache: successful syncs of external subtitle files are remembered
        // so repeat library sweeps skip unchanged content whose output still exists.
        if (job.Status == SyncJobStatus.Completed)
        {
            RecordSweepOutcome(job, ok: true, outputPath: job.OutputPath, error: null);
        }
    }

    /// <summary>
    /// Describes what a successful sync actually changed, by comparing cue
    /// timings of the original and the synced file: the applied offset in
    /// milliseconds (signed; + = subtitles moved later) and, when ffsubsync
    /// corrected a framerate mismatch, the fitted time ratio plus the total
    /// cumulative drift it fixed over the subtitle's runtime.
    /// </summary>
    /// <summary>
    /// States what the engine said about an alignment: its score and the offset it chose.
    /// </summary>
    /// <remarks>
    /// Logged for both paths on purpose. The score was being discarded, which is why a wrong ruler could pass
    /// without anything in the log hinting at it, and the two numbers side by side are what lets a field run be
    /// read after the fact: a subtitle reference that scores far below the audio reference of the same file is
    /// the pattern S31 is about.
    /// </remarks>
    /// <param name="jobId">The job the run belonged to.</param>
    /// <param name="reference">What the engine was aligned against, in words.</param>
    /// <param name="score">The score it printed, when it printed one.</param>
    /// <param name="offsetSeconds">The offset it printed, when it printed one.</param>
    private static void LogEngineAlignment(string jobId, string reference, double? score, double? offsetSeconds)
    {
        if (score is null && offsetSeconds is null)
        {
            return;
        }

        var scoreText = score is { } s ? $"{s:0.###}" : "(none)";
        var offsetText = offsetSeconds is { } o ? $"{o:0.000} s" : "(none)";
        PluginLog.Info(
            $"[{jobId}] ffsubsync alignment: score={scoreText} offset={offsetText} against {reference}"
            + (score is { } low && low < 0
                ? " - the engine itself calls a negative score an unsuccessful sync"
                : string.Empty));
    }

    /// <summary>
    /// The VAD to hand the engine for a reference: the audio VAD when the reference is the audio, and the
    /// configured method when the plugin supplied a subtitle it has already vetted.
    /// </summary>
    /// <param name="referenceSpec">The subtitle reference in use, or null when the reference is the audio.</param>
    /// <returns>The VAD to pass, or null to use the configured one.</returns>
    internal static string? VadForReference(string? referenceSpec)
        => referenceSpec is null ? AudioReferenceVad : null;

    /// <summary>Gets the VAD the engine is given when the plugin intends the audio to be the reference.</summary>
    internal static string AudioReferenceVadName => AudioReferenceVad;

    /// <summary>
    /// States that the engine is being given a VAD it did not get from the configuration, and why.
    /// </summary>
    /// <remarks>
    /// Logged because this is the difference between "the audio path" and "a path the engine chose a subtitle
    /// for": a field run has to be able to tell which signal produced an answer (S43).
    /// </remarks>
    /// <param name="jobId">The job the run belongs to.</param>
    /// <param name="reference">What the engine is aligned against, in words.</param>
    /// <param name="configured">The method the configuration names.</param>
    private static void LogVadOverride(string jobId, string reference, string? configured)
    {
        if (string.Equals(configured, AudioReferenceVad, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        PluginLog.Info(
            $"[{jobId}] reference is {reference}, so the engine is given --vad {AudioReferenceVad}: with the "
            + $"configured '{configured ?? "subs_then_webrtc"}' it would read the video's own subtitle tracks as its "
            + "speech signal, and which track that is, is the engine's choice rather than the plugin's");
    }

    /// <summary>
    /// Reads the alignment score out of one line of the engine's output.
    /// </summary>
    /// <remarks>
    /// ffsubsync prints `score: 198713.000` and `offset seconds: 0.050` for every run, and that score is the only
    /// place the engine states how well the two timelines agreed. Measured on 2026-09-15: a real sibling
    /// subtitle scores ~198 700 against the same cut and a subtitle of another film ~2 900, but the *same track
    /// from a 2 % longer cut* scores 274 700, i.e. higher than the correct ruler, so the score alone is not a
    /// verdict - it is a number the log has to carry, and one signal beside the spread above. It is captured
    /// because it was being discarded, and because a field run cannot be read without it.
    /// </remarks>
    /// <param name="line">One line of the engine's stdout/stderr.</param>
    /// <param name="score">The score it states, when it states one.</param>
    /// <returns>True when the line carried a score.</returns>
    internal static bool TryParseEngineScore(string line, out double score)
    {
        score = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var marker = line.IndexOf("score:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var rest = line[(marker + "score:".Length)..].TrimStart();
        var end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] is '-' or '+' or '.' or ','))
        {
            end++;
        }

        return end > 0 && double.TryParse(
            rest[..end].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out score);
    }

    /// <summary>
    /// Reads the offset the engine reported out of one line of its output.
    /// </summary>
    /// <param name="line">One line of the engine's output.</param>
    /// <param name="seconds">The offset it states, when it states one.</param>
    /// <returns>True when the line carried an offset.</returns>
    internal static bool TryParseEngineOffset(string line, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(line))
        {
            return false;
        }

        var marker = line.IndexOf("offset seconds:", StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            return false;
        }

        var rest = line[(marker + "offset seconds:".Length)..].TrimStart();
        var end = 0;
        while (end < rest.Length && (char.IsDigit(rest[end]) || rest[end] is '-' or '+' or '.' or ','))
        {
            end++;
        }

        return end > 0 && double.TryParse(
            rest[..end].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
    }

    private async Task RunSyncJob(
        SyncJob job,
        Video video,
        MediaBrowser.Model.Entities.MediaStream subtitleStream,
        int subtitleOrdinal,
        Configuration.PluginConfiguration config,
        CancellationToken cancellationToken)
    {
        job.Status = SyncJobStatus.Running;
        job.StartedAtUtc = DateTime.UtcNow;
        job.Progress = 0.0;

        var videoPath = video.Path;
        var videoDir = Path.GetDirectoryName(videoPath) ?? ".";
        var videoNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var videoExt = Path.GetExtension(videoPath);
        var tempDir = Path.Combine(Plugin.Instance?.TempPath ?? Path.GetTempPath(), job.Id);

        // Stream of the media file ffsubsync should take its speech signal from. Embedded
        // inputs set this so the subtitle being fixed is not used as its own reference.

        // True when this job is aligned against a subtitle taken from a sibling track instead of
        // against the audio. Such a result is only ever as good as that track, so it is checked
        // before anything is written.
        var stretchDropped = false;
        var audioFallback = false;
        var wideAllowanceApplied = false;

        // Which track became the reference, for both log lines and the outcome text.

        // Duration of the video, used to judge whether a track is a full subtitle or just signs.
        var videoDurationForReference = video.RunTimeTicks is { } refTicks && refTicks > 0
            ? TimeSpan.FromTicks(refTicks)
            : TimeSpan.Zero;

        // Paths for the safe atomic-replace workflow
        // Filled in by the write step as it goes, and read by the failure path below: a write that throws
        // returns nothing, so the backup it created has to be published where the catch can still see it.
        var write = new SyncWriteOutcome();
        string? tempOutput = null;   // ffsubsync output in temp dir
        // Declared outside the try: the audio reference is prepared from four places, two of them outside
        // reference resolution, and the finally below drops the analysis link this object describes.
        var reference = new ReferenceResolution { Path = videoPath };
        string? changedDir = null;   // folder touched by this job (for the targeted library rescan)
        string? cuesNote = null;     // set when the subtitle looks like a signs/forced track

        // The job's own audio analysis, if it makes one. Declared here because the link's lifetime is the
        // job's lifetime: see the comment on the drop in the finally below (S46).

        try
        {
            // Everything that can throw lives inside this block, including the checks below: a job that
            // threw before reaching it skipped its own error handling and its cleanup, which is one of the
            // two ways a job stayed Running for good (B6).
            //
            // Step 0: Ensure ffsubsync is available
            job.Phase = "Preparing";
            job.Progress = 0.0;

            // Every job passes here, and a job about to run is the right place to notice that nothing has measured
            // its volume: the next planning pass can then judge that volume on a measurement instead of holding it.
            // The first attempt at this sat in the audio-reference branch, which jobs on a real server do not take -
            // 17 engine runs on 2026-09-14, several on the fast volume, and no probe ever fired (S38).
            ProbeVolumeIfUnmeasured(
                _jobContexts.TryGetValue(job.Id, out var probeContext) ? probeContext.Video.Path : null);

            Directory.CreateDirectory(tempDir);

            // The extracted subtitle is not the job's private business: every job of this file reads the same
            // tracks out of it, and it outlives the job that happened to extract it (see SharedExtractionStore).
            var sharedExtractDir = SharedExtractionStore.Acquire(video.Path, job.Id);

            // The file is checked here instead of when the task is queued: queueing must not touch the
            // media share (a stat per task slowed a 50-task batch to a minute while a job was reading the
            // same share, which starved the scheduler). One stat per job, at the point where it matters.
            if (!File.Exists(videoPath))
            {
                throw new FileNotFoundException($"The video file is no longer on disk: {videoPath}");
            }

            var ffsubsyncExe = ResolveFfSubSyncPath();

            if (!File.Exists(ffsubsyncExe) && ffsubsyncExe != "ffsubsync")
            {
                throw new InvalidOperationException($"ffsubsync not found at '{ffsubsyncExe}'. Install it from the plugin configuration page.");
            }

            // Step 1: Prepare subtitle input
            string subtitleInputPath;

            // Language filter applies to external files and embedded tracks alike;
            // ListSubtitles already hides what the filter excludes, so reaching this
            // point means a stale client queued the track.
            var allowedLanguages = Services.SettingsSource.Current()?.SyncLanguages ?? Array.Empty<string>();
            if (!LanguageSupport.MatchesFilter(subtitleStream.Language, allowedLanguages))
            {
                throw new InvalidOperationException(
                    $"This subtitle track is in {LanguageSupport.Label(subtitleStream.Language)}, which the configured language filter ({LanguageSupport.Describe(allowedLanguages)}) excludes.");
            }

            if (subtitleStream.IsExternal && !string.IsNullOrEmpty(subtitleStream.Path))
            {
                subtitleInputPath = subtitleStream.Path;

                // An external sidecar has no track of its own inside the file, but the file it sits next
                // to usually has one, and using it makes the alignment exact instead of an audio guess.
                // No sibling: the audio is the ruler, which for an external file is what the user asked
                // for and is the one case where an unverifiable result is still written (S8, settled
                // with the user on 2026-09-11).
                var siblings = video.GetMediaSources(true)
                    .SelectMany(source => source.MediaStreams)
                    .Where(stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle
                        && !stream.IsExternal)
                    .ToList();
                if (siblings.Count > 0)
                {
                    reference.Stream = MediaStreamMap.SelectReferenceStream(
                        false,
                        siblings.Select(stream => stream.Codec ?? string.Empty).ToList(),
                        -1,
                        siblings.Select(stream => stream.IsForced).ToList());
                    _logger.LogInformation(
                        "External sync of {Subtitle}: aligning against '{Reference}' of {Video} instead of the audio",
                        subtitleInputPath,
                        reference.Stream,
                        videoPath);
                }
            }
            else
            {
                // Only text subtitles can be aligned — image-based tracks (PGS,
                // DVD/VobSub, DVB, XSUB) cannot be converted to SRT text and made
                // ffmpeg fail with a cryptic exit code (e.g. 234) at extraction.
                if (LanguageSupport.IsImageBased(subtitleStream.Codec))
                {
                    throw new InvalidOperationException(
                        "This embedded subtitle track is image-based (PGS/DVD/VobSub) and can't be synchronized — only text subtitles can be aligned.");
                }

                job.Phase = "Extracting subtitle";
                job.Progress = 0.05;
                subtitleInputPath = Path.Combine(sharedExtractDir, $"subtitle_{job.SubtitleIndex}.srt");

                // Jellyfin's MediaStream.Index cannot be trusted as a container
                // stream index (observed values pointing past the file's real
                // stream count on files mixing embedded + external tracks), so
                // the real stream is located by probing the file with ffmpeg
                // and matching by position among embedded subtitle streams.
                var (containerIndex, subtitleStreamOrdinal, subtitleCodecs) = await MediaStreamMap.ResolveContainerSubtitleIndexAsync(video, subtitleStream, ResolveFfmpegPath(), _processes).ConfigureAwait(false);

                // Keep ffsubsync from using the very track we are fixing as its speech
                // signal (see SelectReferenceStream) — that would report every embedded
                // subtitle as already in sync.
                var embeddedSubtitleStreams = video.GetMediaSources(true)
                    .SelectMany(source => source.MediaStreams)
                    .Where(stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !stream.IsExternal)
                    .ToList();

                reference.Stream = MediaStreamMap.SelectReferenceStream(
                    true,
                    subtitleCodecs,
                    subtitleStreamOrdinal,
                    embeddedSubtitleStreams.Count == subtitleCodecs.Count
                        ? embeddedSubtitleStreams.Select(stream => stream.IsForced).ToList()
                        : null);
                _logger.LogInformation(
                    "Embedded sync of {Video}: deriving the speech signal from '{Reference}'",
                    videoPath, reference.Stream);

                var extractionWatch = System.Diagnostics.Stopwatch.StartNew();
                var extractionMethod = await ExtractEmbeddedAsync(
                    videoPath,
                    subtitleStreamOrdinal,
                    containerIndex,
                    subtitleInputPath,
                    config,
                    job,
                    video.RunTimeTicks,
                    cancellationToken).ConfigureAwait(false);
                extractionWatch.Stop();

                // Say which reader produced the subtitle and what it cost. The indexed reader touches
                // kilobytes and finishes in milliseconds; the ffmpeg fallback demuxes the whole file,
                // which on a NAS is the difference between a second and minutes per subtitle. Putting
                // it in the task result means the difference is visible without the server log.
                job.ExtractionNote = extractionMethod switch
                {
                    "ffmpeg" => $"demuxed with ffmpeg, {extractionWatch.ElapsedMilliseconds} ms",
                    "matroska-cached" => "reused from the pass that read this file for another subtitle",
                    // A cache hit reads nothing at all this run: saying "read through the container index" here
                    // described the reader that originally produced the text, not what this job did (nothing).
                    "subtitle-cache" => "served from the extracted-subtitle cache, no read this run",
                    _ => $"read through the container index ({extractionMethod}), {extractionWatch.ElapsedMilliseconds} ms"
                };

                // A kill during extraction must not turn into "try the next method".
                cancellationToken.ThrowIfCancellationRequested();
                job.Phase = "Extracted subtitle with " + DescribeExtraction(extractionMethod);
            }

            // Step 2: Run ffsubsync → temp output
            job.Phase = "Analyzing speech";
            job.Progress = 0.1;

            // A full episode subtitle carries hundreds of cues. A handful over a long video is a
            // forced/signs track, and nothing else in the output shows it: the synced sidecar looks
            // perfectly normal, it just contains two lines. Reported from real use after a two-cue
            // Norwegian track was synced while the same file also carried a full WebVTT track in that
            // language, so it is logged and stated in the outcome rather than left to be discovered.
            var inputCues = AlignmentMetrics.CountSubtitleCues(subtitleInputPath);
            var videoDuration = video.RunTimeTicks is { } ticks && ticks > 0
                ? TimeSpan.FromTicks(ticks)
                : TimeSpan.Zero;
            if (AlignmentMetrics.LooksLikeSignsTrack(inputCues, videoDuration))
            {
                cuesNote = $"only {inputCues} cue{(inputCues == 1 ? "" : "s")} in a "
                    + $"{(int)videoDuration.TotalMinutes}-minute file \u2014 looks like a forced/signs track, "
                    + "not the full subtitle";
                _logger.LogWarning("Sync job {JobId}: {Note}", job.Id, cuesNote);
            }

            tempOutput = Path.Combine(tempDir, "synced.srt");

            // "fast" mode: the speech analysis depends only on the media file, the VAD
            // method and the ffsubsync build — not on which subtitle is being synced —
            // so it is computed once and reused for the other subtitles of that file.

            // P3: which ruler this job is measured against - the file's own audio, or a sibling subtitle track
            // built here from text this process already has.
            await ResolveReferenceAsync(reference, job, videoPath, videoDurationForReference, cancellationToken)
                .ConfigureAwait(false);

            // The engine is given a rescaled copy when a frame rate difference stands between the two subtitles;
            // the measurement below still compares the original subtitle with the output, so the rescale is
            // visible to the guard rather than hidden from it.
            var engineInput = subtitleInputPath;
            var referenceArg = reference.Path!;
            if (reference.UsedSubtitleReference && config.FixFramerate)
            {
                engineInput = AlignmentMetrics.RescaleOntoReferenceSpan(subtitleInputPath, referenceArg, videoDuration, tempDir, job, _logger) ?? engineInput;
            }

            // P5: the engine run - the first attempt and, when a cached speech analysis turned out to be
            // unusable, a second one from the audio. Everything it produces is consumed inside it except
            // reference.SerializeSpeech, which its retry can flip.
            reference.SerializeSpeech = await RunEngineAttemptAsync(
                job,
                config,
                ffsubsyncExe,
                videoPath,
                referenceArg,
                reference.Spec,
                reference.UsedSubtitleReference,
                engineInput,
                tempOutput,
                tempDir,
                reference.SerializeSpeech,
                reference.Stream,
                reference.UsingCachedSpeech,
                reference.SpeechKey,
                reference.Path,
                cancellationToken).ConfigureAwait(false);


            // A stretch is a claim about the whole timeline, and only the film's audio can test it: another
            // subtitle shows the same few percent whether the subtitle is from a different framerate or from a
            // different cut. Runs only when something was stretched.
            if (engineInput != subtitleInputPath)
            {
                var (verifiedPath, dropped, verifiedInput) = await VerifyStretchAgainstAudioAsync(
                    job,
                    config,
                    ffsubsyncExe,
                    videoPath,
                    engineInput,
                    subtitleInputPath,
                    tempDir,
                    videoDuration.TotalSeconds,
                    cancellationToken).ConfigureAwait(false);
                stretchDropped = dropped;
                if (verifiedPath is not null)
                {
                    tempOutput = verifiedPath;
                    engineInput = verifiedInput;
                }
                else if (dropped)
                {
                    job.Outcome = "the stretch did not hold against the audio and the offset-only alignment produced nothing";
                }
            }

            if (!File.Exists(tempOutput) && engineInput != subtitleInputPath && File.Exists(engineInput))
            {
                // The engine suppressed its write because the subtitle it was handed - the copy the plugin
                // rescaled onto the reference's time base - needed no further shift. That copy is the fix the
                // user asked for: the rescaled subtitle becomes the result, instead of the run reporting
                // "already in sync" while the user's own PAL-timed file is left as it was. The checks below see
                // it exactly as they would an engine output, including the pair rule.
                File.Copy(engineInput, tempOutput, overwrite: true);
                PluginLog.Info(
                    $"[{job.Id}] framerate: the engine needed no further shift on the rescaled subtitle "
                    + "\u2014 that rescaled timing is the result");
                _logger.LogInformation(
                    "Sync job {JobId}: the subtitle was rescaled onto the reference's time base and aligned; using it as the output",
                    job.Id);
            }

            if (!File.Exists(tempOutput))
            {
                // ffsubsync suppresses writing when the detected shift is below
                // its threshold (default 3 s) — the subtitle is effectively
                // already in sync, so this is a success, not a failure.
                CompleteAlreadyInSync(job, cuesNote);
                return;
            }

            _logger.LogInformation("ffsubsync produced synced subtitle ({Size} bytes)", new FileInfo(tempOutput).Length);

            // ffsubsync writes an output file even when the timings come out identical, which produced
            // a ".SYNCED" sidecar with the same timing as its source: no benefit, one more subtitle
            // track in the library. Measured before anything is written, so nothing is touched.
            // Measured against what the engine was actually given. When the plugin rescaled a framerate-mismatched
            // subtitle itself (engineInput differs), comparing the original with the output made a correct fix look
            // like a growing shift - the median difference between two differently scaled timelines is about half
            // the file's drift, 55.4 s on the 50-minute fixture - and the reference ceiling refused a file that had
            // just been fixed. Comparing the engine's own input keeps that check about the alignment.
            var measured = AlignmentMetrics.MeasureSyncChange(engineInput, tempOutput);

            // A shift that came from a subtitle reference is only ever as good as that track: a
            // reference taken from a different cut drags every subtitle of the file onto it, and the
            // file that comes out looks exactly like an ordinary success. AGENTS.md has documented
            // MaxSubtitleReferenceOffsetSeconds as a refusal since the reference path was added, so
            // this is that refusal — with the measured numbers, and without touching anything.
            var referenceCeilingMs = Math.Max(1.0, Configuration.SettingsValidation.MaxSubtitleReferenceOffsetSecondsOf(config)) * 1000.0;

            // ...and a median is not enough to tell a ruler that fits from one that does not. Measured on
            // 2026-09-15: a subtitle aligned against the *same track from a 2 % longer cut* came out with a median
            // shift of -26,52 s - under the 30 s ceiling above, so nothing refused it - while its per-cue
            // displacement had an interquartile range of 27,76 s; the same file against its real sibling track
            // measured an IQR of 0,00 s. The spread is what separates them, and it is the only signal in the
            // plugin that sees this case: the engine's own score rated the wrong ruler *higher* (274 721 against
            // 198 713). So a subtitle ruler is also refused when its cues did not move together.
            var spreadCeilingMs = referenceCeilingMs * AlignmentMetrics.SubtitleReferenceSpreadFraction;
            var rulerSpreadTooWide = measured is { } spread && AlignmentMetrics.RulerSpreadTooWide(spread, referenceCeilingMs);
            if (reference.UsedSubtitleReference
                && measured is { } fromReference
                && (Math.Abs(fromReference.ShiftMs) > referenceCeilingMs || rulerSpreadTooWide))
            {
                // The measurement is right and the conclusion was incomplete: a subtitle reference cannot be trusted
                // for a shift this size (it is very likely from a different cut), but the film's own audio cannot be
                // a wrong cut at all. Instead of refusing the subtitle because of the track it happened to be
                // aligned against, drop that track as a ruler and align this subtitle against the audio. The track
                // is discarded so the file's other subtitles do not repeat the same measurement, and the audio
                // analysis is paid once per file, cached like every other audio path.
                var detail = Math.Abs(fromReference.ShiftMs) > referenceCeilingMs
                    ? $"aligned to the reference subtitle {reference.Spec} at {fromReference.ShiftMs} ms"
                    : $"the cues did not move together against {reference.Spec}: a spread of "
                        + $"{fromReference.SpreadMs / 1000.0:0.00} s across the middle half of its cues "
                        + $"(range {fromReference.RangeMs / 1000.0:0.00} s) while the median was "
                        + $"{fromReference.ShiftMs} ms";
                _logger.LogWarning(
                    "Sync job {JobId}: the reference subtitle is not the same cut ({Detail}) - aligning against the audio instead",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id}: the reference subtitle {reference.Spec} is not the same cut ({detail}, over the "
                    + $"{referenceCeilingMs / 1000.0:0.#} s limit for a subtitle reference, itself under the "
                    + $"{spreadCeilingMs / 1000.0:0.#} s spread limit) \u2014 discarding that track as a ruler and "
                    + $"aligning against the audio instead, file={video.Path}");

                if (reference.Spec is not null)
                {
                    ReferenceStore.Discard(videoPath, reference.Spec);
                }

                var audioReference = await PrepareAudioReferenceAsync(
                    reference, job, videoPath,
                    "the reference subtitle is not the same cut as the video", cancellationToken).ConfigureAwait(false);

                // S45: the audio retry writes to a path of its own. It used to be handed `tempOutput` - the file
                // the *discarded* ruler's run had just written - and the test afterwards was
                // `audioExit == 0 && File.Exists(tempOutput)`, so an audio run that exited 0 without writing
                // (the engine suppresses its write when the shift is under its threshold, which is exactly what
                // "this subtitle already matches the film" looks like) left that stale file in place: the job
                // then wrote the discarded ruler's answer and reported it as the audio's. With a path of its own,
                // "did the audio write this?" is answerable again, because nothing else can have. The wide-window
                // and cross-check retries below have always used their own paths for the same reason.
                var audioOutput = Path.Combine(tempDir, "audio-fallback.srt");
                SafeDelete(audioOutput);
                var audioArgs = BuildFfSubSyncArgs(
                    config, audioReference, subtitleInputPath, audioOutput, tempDir, reference.SerializeSpeech, null,
                    vadOverride: AudioReferenceVad);

                double? audioScore = null;
                double? audioOffsetSeconds = null;
                var audioExit = await _processes.RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe, audioArgs, tempDir,
                    line =>
                    {
                        if (TryParseEngineScore(line, out var parsedScore))
                        {
                            audioScore = parsedScore;
                        }

                        if (TryParseEngineOffset(line, out var parsedOffset))
                        {
                            audioOffsetSeconds = parsedOffset;
                        }
                    },
                    cancellationToken,
                    new EngineWatch(job.Id, Path.GetFileName(videoPath), "audio")).ConfigureAwait(false);
                LogEngineAlignment(job.Id, "the audio", audioScore, audioOffsetSeconds);

                if (audioExit == 0 && File.Exists(audioOutput))
                {
                    if (reference.SpeechKey is not null && reference.SerializeSpeech)
                    {
                        SpeechCache.Harvest(audioReference, reference.SpeechKey);
                        SpeechCache.Prune();
                    }

                    ReleaseSpeechGate(job, videoPath);
                    reference.UsedSubtitleReference = false;
                    reference.Spec = null;
                    reference.Stream = null;
                    referenceArg = audioReference;
                    audioFallback = true;
                    tempOutput = audioOutput;
                    measured = AlignmentMetrics.MeasureSyncChange(subtitleInputPath, tempOutput);
                    PluginLog.Info(
                        $"[{job.Id}] reference: method=audio why=the reference subtitle was not the same cut "
                        + $"(it demanded {fromReference.ShiftMs} ms)");
                }
                else
                {
                    _logger.LogWarning(
                        "Sync job {JobId}: the audio alignment after the bad reference did not produce a subtitle (exit {Code}) - nothing written",
                        job.Id,
                        audioExit);
                    PluginLog.Info(
                        $"job {job.Id} REFUSED: {detail}, over the {referenceCeilingMs / 1000.0:0.#} s limit for a "
                        + "subtitle reference, and the audio alignment that replaced it produced nothing; nothing "
                        + $"written, source untouched, file={video.Path}");
                    RefuseJob(
                        job,
                        "Refused",
                        $"refused: the subtitle was aligned against the file's own subtitle track {reference.Spec}, "
                            + $"which demanded a {fromReference.ShiftMs} ms shift — that track is not the same cut — and "
                            + "aligning against the audio instead produced nothing. Nothing was written.",
                        tempOutput);
                    return;
                }
            }

            // This ceiling is ffsubsync's search window, not a safety limit: an answer outside it cannot be seen, and
            // the engine then returns the best wrong one - which is how a subtitle needing ~112 s came back as 56 s.
            // So a result that reaches the window gets one more alignment with a wider one, and that result is only
            // accepted when it is *not* pinned to the wider window either. A definitive answer, then one alignment
            // against the film's audio as a last check: anything a wide window matched wrongly shows up there as a
            // large remaining shift, and the job refuses with both numbers instead of writing it.
            var ceilingMs = Configuration.SettingsValidation.MaxOffsetSecondsOf(config) * 1000.0;
            if (!wideAllowanceApplied && measured is { } onCeiling && Math.Abs(onCeiling.ShiftMs) >= ceilingMs - 500)
            {
                var wideSeconds = Math.Max(Configuration.SettingsValidation.MaxOffsetSecondsOf(config) * 2, 300);
                var wideLimitMs = wideSeconds * 1000.0;
                var wideOutput = Path.Combine(tempDir, "wide-window.srt");
                SafeDelete(wideOutput);
                var wideArgs = BuildFfSubSyncArgs(
                    config, referenceArg, engineInput, wideOutput, tempDir, reference.SerializeSpeech, reference.Stream,
                    VadForReference(reference.Spec));
                wideArgs[wideArgs.IndexOf("--max-offset-seconds") + 1] =
                    wideSeconds.ToString(CultureInfo.InvariantCulture);

                _logger.LogInformation(
                    "Sync job {JobId}: the result reached the {Window} s search window - aligning again with {Wide} s",
                    job.Id,
                    Configuration.SettingsValidation.MaxOffsetSecondsOf(config),
                    wideSeconds);
                PluginLog.Info(
                    $"[{job.Id}] offsets: the alignment reached the {Configuration.SettingsValidation.MaxOffsetSecondsOf(config)} s search window "
                    + $"(it measured {onCeiling.ShiftMs} ms, which a window that size cannot be trusted to have found) "
                    + $"\u2014 aligning again with {wideSeconds} s and checking the result against the film's audio");

                var wideErrors = new List<string>();
                var wideExit = await _processes.RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe,
                    wideArgs,
                    tempDir,
                    line =>
                    {
                        lock (wideErrors)
                        {
                            wideErrors.Add(line);
                            if (wideErrors.Count > 8)
                            {
                                wideErrors.RemoveAt(0);
                            }
                        }
                    },
                    cancellationToken,
                    new EngineWatch(job.Id, Path.GetFileName(videoPath), reference.Stream ?? "(default)")).ConfigureAwait(false);

                var wideChange = wideExit == 0 && File.Exists(wideOutput)
                    ? AlignmentMetrics.MeasureSyncChange(engineInput, wideOutput)
                    : null;

                if (wideChange is { } wider && Math.Abs(wider.ShiftMs) < wideLimitMs - 500)
                {
                    // The wider window produced an answer inside itself: the engine is not clamped any more.
                    var verifyOutput = Path.Combine(tempDir, "wide-check.srt");
                    SafeDelete(verifyOutput);
                    var verifyArgs = BuildFfSubSyncArgs(
                        config, referenceArg, wideOutput, verifyOutput, tempDir, reference.SerializeSpeech, reference.Stream,
                        VadForReference(reference.Spec));
                    // A check, not a search: the configured window and no rescaling.
                    verifyArgs[verifyArgs.IndexOf("--max-offset-seconds") + 1] =
                        Configuration.SettingsValidation.MaxOffsetSecondsOf(config).ToString(CultureInfo.InvariantCulture);
                    foreach (var flag in FramerateArgs(false, false))
                    {
                        if (!verifyArgs.Contains(flag))
                        {
                            verifyArgs.Add(flag);
                        }
                    }

                    verifyArgs.Remove("--gss");

                    var verifyExit = await _processes.RunProcessWithStderrCallbackAsync(
                        ffsubsyncExe, verifyArgs, tempDir, null, cancellationToken,
                        new EngineWatch(job.Id, Path.GetFileName(videoPath), reference.Stream ?? "(default)")).ConfigureAwait(false);
                    var residual = verifyExit == 0 && File.Exists(verifyOutput)
                        ? AlignmentMetrics.MeasureSyncChange(wideOutput, verifyOutput)
                        : null;
                    var residualRatio = residual?.Ratio ?? 1.0;
                    var residualShift = residual?.ShiftMs ?? 0;

                    if (verifyExit == 0
                        && AlignmentMetrics.AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds))
                    {
                        wideAllowanceApplied = true;
                        tempOutput = wideOutput;
                        measured = wider;
                        PluginLog.Info(
                            $"[{job.Id}] offsets: the {wider.ShiftMs} ms result holds against the film's audio "
                            + $"(a further {residualShift} ms, no rescale) \u2014 writing it");
                        _logger.LogInformation(
                            "Sync job {JobId}: the wider-window result holds against the audio ({Shift} ms more)",
                            job.Id,
                            residualShift);
                    }
                    else
                    {
                        var why = verifyExit != 0
                            ? $"the check did not run (exit {verifyExit})"
                            : $"the film's audio still asked for {residualShift} ms more (ratio {residualRatio:0.0000})";
                        _logger.LogWarning(
                            "Sync job {JobId}: refusing after the wider window ({Why}) — nothing written",
                            job.Id,
                            why);
                        PluginLog.Info(
                            $"job {job.Id} REFUSED: this subtitle needed {onCeiling.ShiftMs} ms with a "
                            + $"{Configuration.SettingsValidation.MaxOffsetSecondsOf(config)} s window and {wider.ShiftMs} ms with {wideSeconds} s, and {why}; "
                            + $"nothing written, source untouched, file={video.Path}");
                        RefuseJob(
                            job,
                            "Refused",
                            $"refused: this subtitle is further out than the {Configuration.SettingsValidation.MaxOffsetSecondsOf(config)} s search "
                                + $"window ({onCeiling.ShiftMs} ms reached it), the {wideSeconds} s window measured "
                                + $"{wider.ShiftMs} ms, and that did not hold up against the film's audio: {why}. Nothing "
                                + "was written.",
                            tempOutput);
                        return;
                    }
                }
                else
                {
                    string engineTail;
                    lock (wideErrors)
                    {
                        engineTail = wideErrors.Count == 0
                            ? string.Empty
                            : " · engine said: " + string.Join(" | ", wideErrors).Trim();
                    }

                    var detail = wideChange is { } stillClamped
                        ? $"the {wideSeconds} s window also reached its limit ({stillClamped.ShiftMs} ms)"
                        : $"the {wideSeconds} s window produced nothing (exit {wideExit}){engineTail}";

                    // S22: the engine's own words decide which refusal this is. A run that could not open the
                    // reference it was handed is not a search-window problem, and the field's log shows this was
                    // 7 of 8 refusals - every one of them sending the user to "Maximum offset", a setting that
                    // cannot help a file that could not be read.
                    if (EngineCouldNotReadReference(engineTail))
                    {
                        _logger.LogWarning(
                            "Sync job {JobId}: refusing after the wider window - the engine could not read the reference it was handed ({Detail})",
                            job.Id,
                            detail);
                        PluginLog.Info(
                            $"job {job.Id} REFUSED: the engine could not read the reference it was handed "
                            + $"({referenceArg}){engineTail}; this is not a search-window problem, so \"Maximum offset\" "
                            + $"is not the setting to change; nothing written, source untouched, file={video.Path}");
                        RefuseJob(
                            job,
                            "Refused",
                            "refused: the engine could not read the reference it was aligned against "
                                + $"({referenceArg}){engineTail}. Nothing was written. Raising \"Maximum offset\" will "
                                + "not help this - the reference could not be opened, so the alignment had nothing to "
                                + "measure against.",
                            tempOutput);
                        return;
                    }

                    _logger.LogWarning(
                        "Sync job {JobId}: refusing after the wider window ({Detail}) — nothing written",
                        job.Id,
                        detail);
                    PluginLog.Info(
                        $"job {job.Id} REFUSED: this subtitle is further out than the plugin is searching "
                        + $"({detail}); raise \"Maximum offset\" and run it again, or sync it by hand. Nothing "
                        + $"written, source untouched, file={video.Path}");
                    RefuseJob(
                        job,
                        "Refused",
                        $"refused: {detail}. Raise \"Maximum offset\" in the plugin settings (it is the search "
                            + "window the alignment may look in) and run it again. Nothing was written.",
                        tempOutput);
                    return;
                }
            }

            // Nothing destructive is ever written: a measured rescale that was not asked for (or that
            // is not a real framerate pair) means the engine moved the timeline, and the source
            // subtitle stays untouched while the job says exactly why. A *piecewise* answer is the one
            // exception, and only when the plugin asked for one (C2): see PiecewiseHolds.
            var piecewiseRequested = Configuration.SettingsValidation.SplitPenaltyOf(config) > 0;
            var structure = piecewiseRequested && measured is not null
                ? AlignmentMetrics.MeasureSegmentStructure(engineInput, tempOutput, AlignmentMetrics.EngineSampleMs)
                : null;
            var piecewiseHolds = structure is { } pieces
                && AlignmentMetrics.PiecewiseHolds(pieces, spreadCeilingMs, Configuration.SettingsValidation.MaxOffsetSecondsOf(config) * 1000.0);
            if (piecewiseHolds)
            {
                _logger.LogInformation(
                    "Sync job {JobId}: the piecewise reading holds ({Segments} segment(s), {Spread} ms within a segment at most) - writing it",
                    job.Id,
                    structure!.Value.Segments,
                    structure.Value.MaxWithinSpreadMs);
                PluginLog.Info(
                    $"[{job.Id}] offsets: the engine's answer is piecewise ({structure.Value.Segments} segment(s), at most "
                    + $"{structure.Value.MaxWithinSpreadMs} ms of spread inside one, largest step {structure.Value.LargestShiftMs} ms) "
                    + $"- the linear reading of it is {measured!.Value.Ratio:0.0000}x, which is a step and not a rescale");
            }

            if (measured is { } scaled
                && !piecewiseHolds
                && !AlignmentMetrics.IsRescaleAcceptable(scaled.Ratio, scaled.ShiftMs, Configuration.SettingsValidation.MaxOffsetSecondsOf(config), config.FixFramerate))
            {
                var span = videoDuration > TimeSpan.Zero
                    ? videoDuration.TotalSeconds
                    : (double?)null;
                var detail = span is null
                    ? scaled.Describe()
                    : $"{scaled.Describe()} over a {span.Value / 60.0:0.0}-minute file";
                _logger.LogWarning(
                    "Sync job {JobId}: refusing a rescaled result ({Detail}) \u2014 nothing written",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id} REFUSED: measured {detail} \u2014 framerate correction is "
                    + (config.FixFramerate ? "on but this is not a framerate pair" : "off")
                    + $"; nothing written, source untouched, file={video.Path}");
                RefuseJob(
                    job,
                    "Refused",
                    $"refused: the engine rescaled the timings ({detail}) and nothing was written. "
                        + (config.FixFramerate
                            ? "This is not a framerate pair a release could really have."
                            : "Turn on \"Correct framerate mismatch\" only for subtitles from a different framerate."),
                    tempOutput);
                return;
            }

            var suspiciousMs = referenceCeilingMs * SuspiciousReferenceShiftFraction;
            var agreementMs = referenceCeilingMs * AlignmentMetrics.SubtitleReferenceAudioAgreementFraction;
            if (reference.UsedSubtitleReference
                && measured is { } fromReferenceNote
                && Math.Abs(fromReferenceNote.ShiftMs) > suspiciousMs)
            {
                // The plugin's own words for a shift this size are "usually means that track is not the same cut",
                // and until now it wrote the result anyway with that note on it. Instead, ask the one ruler that
                // cannot be a different cut - the film's own audio - and let the two answers decide. It costs one
                // audio analysis, cached per file like every other audio path, and only for shifts in this band.
                cuesNote = Join(cuesNote, $"aligned to the reference subtitle {reference.Spec} at {fromReferenceNote.ShiftMs} ms - "
                    + "worth checking, a shift this size usually means the reference track is not the same cut");
                _logger.LogInformation(
                    "Sync job {JobId}: aligned to the reference subtitle {Reference} at {Shift} ms",
                    job.Id,
                    reference.Spec,
                    fromReferenceNote.ShiftMs);
                PluginLog.Info(
                    $"[{job.Id}] note: aligned to the reference subtitle {reference.Spec} at {fromReferenceNote.ShiftMs} ms "
                    + "- check the result; a shift this size usually means that track is not the same cut");


                // The ruler's shape score is logged as context only. It was built as a gate in front of this
                // cross-check and rejected, on measurement rather than taste: a ruler that is the same cut as the
                // film but offset from it correlates with the target perfectly and scores exactly like a correct
                // ruler (0.833 on the S31 fixture at +20 s, which is right, and at +25 s, which is wrong), so a
                // score-based skip writes a wrong file in precisely the case this cross-check exists for. The
                // score assumes the peak is centred on the search window too, so a 0.95 bar is unreachable for any
                // ruler that asks for a real shift (0.833 is the ceiling). See docs/EVIDENCE_s31_shape_gate.md.
                var rulerShape = SubtitleRulerShape.Score(
                    engineInput,
                    referenceArg,
                    Configuration.SettingsValidation.MaxOffsetSecondsOf(config));
                PluginLog.Info(
                    $"[{job.Id}] ruler shape: "
                    + (rulerShape is { } shape ? shape.Describe() : "not measurable (the two tracks could not be read as subtitles)")
                    + " \u2014 context only; the audio cross-check runs either way");

                var crossCheckOutput = Path.Combine(tempDir, "audio-cross-check.srt");
                SafeDelete(crossCheckOutput);
                var crossReference = await PrepareAudioReferenceAsync(
                    reference, job, videoPath,
                    "the reference subtitle asked for a shift worth checking", cancellationToken).ConfigureAwait(false);
                // `webrtc`, not the configured VAD: with the default `subs_then_webrtc` the engine takes the
                // video's *embedded subtitles* as the speech signal, and the ruler being cross-checked is one of
                // them - measured 2026-09-15, the "audio" run then returned the ruler's own answer (24 170 ms
                // against the film's real -0,08 s), i.e. it confirmed the very track it was meant to check.
                var crossArgs = BuildFfSubSyncArgs(
                    config, crossReference, subtitleInputPath, crossCheckOutput, tempDir, reference.SerializeSpeech, null,
                    vadOverride: AudioReferenceVad);
                double? crossScore = null;
                double? crossOffset = null;
                PluginLog.Info(
                    $"[{job.Id}] cross-check run: reference={crossReference} input={subtitleInputPath} "
                    + $"args={string.Join(' ', crossArgs)}");
                var crossExit = await _processes.RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe, crossArgs, tempDir,
                    line =>
                    {
                        if (TryParseEngineScore(line, out var parsedCrossScore))
                        {
                            crossScore = parsedCrossScore;
                        }

                        if (TryParseEngineOffset(line, out var parsedCrossOffset))
                        {
                            crossOffset = parsedCrossOffset;
                        }
                    },
                    cancellationToken,
                    new EngineWatch(job.Id, Path.GetFileName(videoPath), "audio")).ConfigureAwait(false);
                LogEngineAlignment(job.Id, "the audio (cross-check of a subtitle ruler)", crossScore, crossOffset);
                PluginLog.Info(
                    $"[{job.Id}] cross-check exit={crossExit}, output={crossCheckOutput} "
                    + $"exists={File.Exists(crossCheckOutput)}");

                var fromAudio = crossExit == 0 && File.Exists(crossCheckOutput)
                    ? AlignmentMetrics.MeasureSyncChange(subtitleInputPath, crossCheckOutput)
                    : null;
                if (fromAudio is { } audioChange)
                {
                    if (reference.SpeechKey is not null && reference.SerializeSpeech)
                    {
                        SpeechCache.Harvest(crossReference, reference.SpeechKey);
                        SpeechCache.Prune();
                    }

                    ReleaseSpeechGate(job, videoPath);
                    var disagreementMs = Math.Abs(audioChange.ShiftMs - fromReferenceNote.ShiftMs);
                    if (AlignmentMetrics.RulersDisagree(fromReferenceNote.ShiftMs, audioChange.ShiftMs, referenceCeilingMs))
                    {
                        // The two rulers disagree about this film, and only one of them can be a different cut.
                        File.Copy(crossCheckOutput, tempOutput, overwrite: true);
                        if (reference.Spec is not null)
                        {
                            ReferenceStore.Discard(videoPath, reference.Spec);
                        }

                        var wasSpec = reference.Spec;
                        reference.UsedSubtitleReference = false;
                        reference.Spec = null;
                        reference.Stream = null;
                        referenceArg = crossReference;
                        audioFallback = true;
                        measured = AlignmentMetrics.MeasureSyncChange(subtitleInputPath, tempOutput);
                        cuesNote = null;
                        _logger.LogWarning(
                            "Sync job {JobId}: the reference subtitle and the film's own audio disagree ({Ruler} ms against {Audio} ms) - writing the audio's answer",
                            job.Id,
                            fromReferenceNote.ShiftMs,
                            audioChange.ShiftMs);
                        PluginLog.Info(
                            $"[{job.Id}] the reference subtitle {wasSpec} and the film's own audio disagree "
                            + $"({fromReferenceNote.ShiftMs} ms against {audioChange.ShiftMs} ms, over the "
                            + $"{agreementMs / 1000.0:0.#} s they are allowed to differ) \u2014 that track is not this "
                            + $"film's timeline, so it is discarded as a ruler and the audio's answer is written, file={video.Path}");
                        PluginLog.Info(
                            $"[{job.Id}] reference: method=audio why=the reference subtitle and the film's own audio "
                            + $"disagreed by {disagreementMs} ms");
                    }
                    else
                    {
                        PluginLog.Info(
                            $"[{job.Id}] the reference subtitle's shift ({fromReferenceNote.ShiftMs} ms) is confirmed by "
                            + $"the film's own audio ({audioChange.ShiftMs} ms, within {agreementMs / 1000.0:0.#} s) "
                            + "- keeping the reference's answer");
                    }
                }
                else
                {
                    PluginLog.Info(
                        $"[{job.Id}] the audio cross-check of the reference subtitle produced no alignment "
                        + $"(exit {crossExit}): the shift stands as noted, nothing new is decided");
                }
            }

            // "Changed nothing" is about the subtitle the user has, so it is measured against that: when the
            // plugin rescaled a framerate-mismatched subtitle, the engine's input and its output are identical by
            // definition, and comparing those two reported "+0 ms offset - no sidecar written" while the user's
            // own file was still PAL-timed. The alignment guards above keep using what the engine was given.
            var changedForUser = engineInput == subtitleInputPath
                ? measured
                : AlignmentMetrics.MeasureSyncChange(subtitleInputPath, tempOutput);

            if (changedForUser is { IsNoChange: true } noChange)
            {
                CompleteAsNoChange(job, noChange, cuesNote, tempOutput);
                return;
            }

            // S8, settled with the user on 2026-09-11: a result whose only ruler was the audio cannot be
            // checked against anything. Measured on this project's own fixture, a subtitle that was
            // already in sync came back "+1780 ms offset" from the audio alone and was written as a
            // plain success. For a track *inside* the file that is a guess, so nothing is written and
            // the job says exactly that. An external sidecar has no other ruler to fall back on — the
            // audio IS its reference — so that path still writes, as it always has.
            if (!reference.UsedSubtitleReference && !subtitleStream.IsExternal)
            {
                var detail = measured is { } audioOnly ? audioOnly.Describe() : "no measurable change";
                _logger.LogWarning(
                    "Sync job {JobId}: refusing to write an audio-only alignment ({Detail}) — nothing written",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id} UNVERIFIED: the audio was the only ruler ({detail}) and this track has no "
                    + $"reference subtitle to check it against; nothing written, source untouched, file={video.Path}");
                RefuseJob(
                    job,
                    "Unverified \u2014 audio-only alignment",
                    $"unverified: this subtitle was aligned against the audio ({detail}) and the file holds "
                        + "no other text track to check that against, so nothing was written. An audio reference on a "
                        + "short file can be out by a second or more. Sync it against a subtitle track if the file has "
                        + "one, or run it from an external .srt, where the audio is the reference the plugin is meant "
                        + "to use.",
                    tempOutput);
                return;
            }

            // P14-P16: guard the engine's output, write the synced subtitle next to the media, and confirm
            // what was written. The two values the rest of the job needs come back; everything else (the
            // target path, the phase, the progress) it records on the job itself.
            await WriteSyncedSubtitleAsync(
                job,
                config,
                subtitleStream,
                videoDir,
                videoNameNoExt,
                videoPath,
                tempOutput,
                write).ConfigureAwait(false);
            changedDir = write.ChangedDir;

            // P17: describe what changed - the offset or rescale factor, the signs note, where the replaced
            // original was kept - and mark the job complete.
            DescribeCompletedSync(
                job,
                subtitleStream,
                subtitleInputPath,
                engineInput,
                write.BackupPath,
                cuesNote,
                audioFallback,
                stretchDropped);

            // P18: announce it (size probe, completion log, folder report, item refresh). Best-effort by
            // design: the subtitle is already on disk, so a library hiccup must not fail a finished job.
            await AnnounceCompletedAsync(job, video, changedDir).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            MarkCancelled(job);
        }
        catch (Exception ex)
        {
            FailJobAndRollBack(job, ex, write.BackupPath);
        }
        finally
        {
            CleanUpAfterJob(job, video, tempDir, reference.SerializeSpeech, reference.SpeechKey);
        }
    }

    /// <summary>
    /// Runs ffsubsync for this job's first attempt and, when a cached speech analysis turned out to be unusable,
    /// a second one from the audio. The stderr callbacks live inside this method on purpose: they mutate only
    /// locals here (the tail of what the engine printed, the score and offset it reported), which is what keeps
    /// the retry safe to read on its own.
    /// </summary>
    /// <returns>Whether the attempt that mattered analysed the speech itself - the caller needs that to know
    /// which speech-cache entry the run belongs to.</returns>
    private async Task<bool> RunEngineAttemptAsync(
        SyncJob job,
        PluginConfiguration config,
        string ffsubsyncExe,
        string videoPath,
        string referenceArg,
        string? referenceSpec,
        bool usedSubtitleReference,
        string engineInput,
        string tempOutput,
        string tempDir,
        bool serializeSpeech,
        string? referenceStream,
        bool usingCachedSpeech,
        string? speechKey,
        string referencePath,
        CancellationToken cancellationToken)
    {
        var args = BuildFfSubSyncArgs(
            config, referenceArg, engineInput, tempOutput, tempDir, serializeSpeech, referenceStream,
            VadForReference(referenceSpec));

        _logger.LogInformation("Running ffsubsync ({Exe}): {Args}", ffsubsyncExe, args);
        PluginLog.Info($"[{job.Id}] ffsubsync start: exe={ffsubsyncExe} cachedSpeech={usingCachedSpeech} reference={referenceStream ?? "(default)"} args={string.Join(' ', args)}");
        LogVadOverride(
            job.Id,
            referenceSpec is null ? "the audio" : $"the subtitle {referenceSpec}",
            config.VadMethod);

        // Parse ffsubsync stderr in real-time for progress updates.
        // tqdm format: " 42%|████▎     | 3000.0/6997.696 [00:27<00:34, 115.36it/s]"
        // Phase messages: "extracting speech...", "computing alignments...", "writing output..."
        var engineWatch = System.Diagnostics.Stopwatch.StartNew();
        // Keep the tail of stderr: "ffsubsync exited with code 1" on its own tells nobody anything,
        // and the reason (an unreadable reference, a subtitle with no text, a demux error) is
        // always in the last few lines ffsubsync printed.
        var engineErrors = new List<string>();
        double? engineScore = null;
        double? engineOffsetSeconds = null;
        var exitCode = await _processes.RunProcessWithStderrCallbackAsync(
            ffsubsyncExe, args, tempDir,
            line =>
            {
                ParseFfSubSyncStderr(line, job);
                    if (TryParseEngineScore(line, out var parsedScore))
                    {
                        engineScore = parsedScore;
                    }

                    if (TryParseEngineOffset(line, out var parsedOffset))
                    {
                        engineOffsetSeconds = parsedOffset;
                    }

                    lock (engineErrors)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            engineErrors.Add(line.Trim());
                            if (engineErrors.Count > 6)
                            {
                                engineErrors.RemoveAt(0);
                            }
                        }
                    }
                },
            cancellationToken,
            new EngineWatch(job.Id, Path.GetFileName(videoPath), referenceStream ?? "(default)")).ConfigureAwait(false);
        engineWatch.Stop();
        PluginLog.Info($"[{job.Id}] ffsubsync exit={exitCode} after {engineWatch.ElapsedMilliseconds} ms");
        LogEngineAlignment(
            job.Id,
            usedSubtitleReference ? $"reference subtitle {referenceSpec}" : "the audio",
            engineScore,
            engineOffsetSeconds);

        // A walk of the media measures its volume without taking any read to measure it, which is the only
        // signal that exists on a run whose extractions were all served from the subtitle cache. It only
        // counts when it is the volume being measured: a walk taken while another volume was being read
        // measures the moment as much as the storage, and believing it held a fast volume to two walks for
        // the rest of a mixed batch on 2026-09-14.
        if (!usedSubtitleReference)
        {
            var walked = MediaLengthOf(videoPath);
            if (walked > 0)
            {
                var walkedVolume = Services.VolumeProfiles.For(videoPath);
                var elsewhere = VolumesOtherThan(
                    MediaVolume.Of(videoPath),
                    _jobs.Values
                        .Where(j => j.Status == SyncJobStatus.Running)
                        .Select(j => MediaVolume.Of(
                            _jobContexts.TryGetValue(j.Id, out var other) ? other.Video.Path : null)));
                var walkedMbPerSec = walked / (engineWatch.ElapsedMilliseconds <= 0 ? 1.0 : engineWatch.ElapsedMilliseconds) / 1000.0;

                if (elsewhere > 0)
                {
                    PluginLog.Info(
                        $"[{job.Id}] this walk moved {walked / 1048576.0:0.0} MB of {videoPath} in "
                        + $"{engineWatch.ElapsedMilliseconds / 1000.0:0.0} s = {walkedMbPerSec:0.0} MB/s, but it is "
                        + $"not being used to judge that volume: {elsewhere} job(s) on another volume were being "
                        + "read at the same time, so this number belongs to the moment rather than to the storage");
                }
                else
                {
                    walkedVolume.ObserveWalk(walked, engineWatch.ElapsedMilliseconds);
                    var walkedCap = WalkCapForProfile(walkedVolume.MsPerCall(), walkedVolume.WalkBytesPerMs());
                    PluginLog.Info(
                        $"[{job.Id}] this walk moved {walked / 1048576.0:0.0} MB of {videoPath} in "
                        + $"{engineWatch.ElapsedMilliseconds / 1000.0:0.0} s = "
                        + $"{(walkedVolume.WalkBytesPerMs() ?? 0) / 1000.0:0.0} MB/s - "
                        + $"the ceiling for that volume is "
                        + $"{(walkedCap.Cap >= int.MaxValue ? "none" : walkedCap.Cap.ToString())} ({walkedCap.Why})");
                }
            }
        }

        if (exitCode != 0 && usingCachedSpeech && speechKey is not null)
        {
            // The cached speech file is unusable (deleted mid-run, truncated, or from
            // a different ffsubsync build). Drop it and redo the run from the audio.
            _logger.LogWarning(
                "Cached speech analysis failed for {Video} (exit code {Code}); falling back to a full audio run",
                videoPath, exitCode);
            var stale = SpeechCache.TryGet(speechKey);
            if (stale is not null)
            {
                try { File.Delete(stale); } catch (IOException) { /* retry below still works */ }
            }

            referencePath = SpeechCache.CreateReferenceLink(videoPath, speechKey);
            serializeSpeech = true;
            args = BuildFfSubSyncArgs(
                config, referenceArg, engineInput, tempOutput, tempDir, serializeSpeech, referenceStream,
                VadForReference(referenceSpec));
            PluginLog.Info($"[{job.Id}] retrying ffsubsync from the audio: {string.Join(' ', args)}");
            lock (engineErrors)
            {
                engineErrors.Clear();
            }

            exitCode = await _processes.RunProcessWithStderrCallbackAsync(
                ffsubsyncExe, args, tempDir,
                line =>
                {
                    ParseFfSubSyncStderr(line, job);
                    lock (engineErrors)
                    {
                        if (!string.IsNullOrWhiteSpace(line))
                        {
                            engineErrors.Add(line.Trim());
                            if (engineErrors.Count > 6)
                            {
                                engineErrors.RemoveAt(0);
                            }
                        }
                    }
                },
                cancellationToken,
                new EngineWatch(job.Id, Path.GetFileName(videoPath), referenceStream ?? "(default)")).ConfigureAwait(false);
            PluginLog.Info($"[{job.Id}] ffsubsync retry exit={exitCode}");
        }

        if (exitCode != 0)
        {
            string why;
            lock (engineErrors)
            {
                why = engineErrors.Count == 0 ? string.Empty : " Last output: " + string.Join(" | ", engineErrors);
            }

            throw new InvalidOperationException($"ffsubsync exited with code {exitCode}.{why}");
        }

        if (speechKey is not null && serializeSpeech)
        {
            // Harvest covers the fallback where ffsubsync wrote the .npz next to the media file. The link
            // itself is not dropped here: the wider-window retry and the verification run later in this
            // same job are handed the same reference, and a retry pointed at a file the plugin deleted
            // under it cannot start (S46 - measured in the field, 7 of one run's 8 refusals). It is
            // dropped once, in this job's finally.
            SpeechCache.Harvest(referencePath, speechKey);
            SpeechCache.Prune();
        }

        ReleaseSpeechGate(job, videoPath);
        return serializeSpeech;
    }

    /// <summary>
    /// Writes the engine's result next to the media and confirms it landed: a dot-separated sidecar in copy mode,
    /// an in-place replace with a backup in replace mode, or a new sidecar for an embedded track. The engine's
    /// output is checked before anything is copied, because copy mode used to leave a 0-byte sidecar in the
    /// library for a job that then failed.
    /// </summary>
    /// <param name="job">The job, whose output path and progress this sets.</param>
    /// <param name="config">The plugin settings, for the copy/replace choice.</param>
    /// <param name="subtitleStream">The track being synced, for its path and language.</param>
    /// <param name="videoDir">The media file's folder, where an embedded track's sidecar goes.</param>
    /// <param name="videoNameNoExt">The media file's name without extension, for that sidecar's name.</param>
    /// <param name="videoPath">The media file, named in the log line for an embedded track.</param>
    /// <param name="tempOutput">The engine's output in the job's temp directory.</param>
    /// <param name="outcome">Filled in as the write proceeds. It is written into rather than returned because the
    /// caller's failure path needs the backup path precisely when the write throws - and a throw returns nothing.</param>
    private async Task WriteSyncedSubtitleAsync(
        SyncJob job,
        PluginConfiguration config,
        MediaBrowser.Model.Entities.MediaStream subtitleStream,
        string videoDir,
        string videoNameNoExt,
        string videoPath,
        string tempOutput,
        SyncWriteOutcome outcome)
    {
        // Step 2b: the result is checked *before* anything is written next to the media. The engine
        // writes an output file even when it holds nothing — an embedded track with no text is the
        // usual case — and copying it first left a 0-byte ".SYNCED.eng.srt" in the library for a job
        // that then failed: the next library scan logs `FfmpegException: ffprobe failed - streams and
        // format are both null` for it, and nothing ever removes it. Nothing is copied until the
        // engine's own output is known to hold subtitles.
        if (!File.Exists(tempOutput) || new FileInfo(tempOutput).Length == 0)
        {
            throw new InvalidOperationException(
                "Subtitle verification failed — synced output is missing or empty. Nothing was written next to "
                + "the media: the engine produced no subtitles for this track.");
        }

        // Step 3: Save the synced subtitle (copy mode by default — original untouched)
        if (subtitleStream.IsExternal && !string.IsNullOrEmpty(subtitleStream.Path))
        {
            if (config.SyncModeCopy)
            {
                job.Phase = "Saving synced copy";
                job.Progress = 0.85;

                var original = subtitleStream.Path;
                var dir = Path.GetDirectoryName(original) ?? ".";
                var stem = Path.GetFileNameWithoutExtension(original);
                // Jellyfin recognises a sidecar only when it starts with the exact media
                // filename and continues with DOT-separated fields (see the media naming
                // docs: "Film.mkv" -> "Film.en.sdh.srt"). A hyphenated marker
                // ("...-SYNCED.srt") leaves Jellyfin unable to associate the file with the
                // video, so nothing appears in the interface. The marker is therefore a
                // field, not part of the name.
                var lang = string.IsNullOrWhiteSpace(subtitleStream.Language)
                    ? null
                    : subtitleStream.Language.Trim().ToLowerInvariant();

                // A stem the plugin already marked loses that marker first (S12): re-syncing its own output updates
                // that file, where appending a second marker wrote a name no player associates with the episode.
                var target = SyncedTargetNaming.SyncedTargetName(dir, stem, lang);

                SyncedTargetNaming.RequireWritable(dir);
                File.Copy(tempOutput, target, overwrite: true);
                job.OutputPath = target;
                outcome.ChangedDir = dir;
                _logger.LogInformation("Synced copy written: {Original} → {Target} (stem={Stem}, lang={Lang}, original untouched)", original, target, stem, lang ?? "(none)");
            }
            else
            {
                job.Phase = "Replacing subtitle";
                job.Progress = 0.85;

                SyncedTargetNaming.RequireWritable(Path.GetDirectoryName(subtitleStream.Path) ?? ".");

                // The backup path is chosen (and therefore known to the rollback below) *before*
                // the original is touched: the destructive copy is inside ReplaceExternalSubtitle,
                // and a failure there used to leave `backupPath` null, so the only rollback there is
                // was skipped for exactly the case that needs it.
                outcome.BackupPath = NextBackupPath(subtitleStream.Path);
                await ReplaceExternalSubtitle(subtitleStream.Path, outcome.BackupPath, tempOutput).ConfigureAwait(false);
                outcome.ChangedDir = Path.GetDirectoryName(subtitleStream.Path) ?? ".";

                job.OutputPath = subtitleStream.Path;
                _logger.LogInformation("Replaced external subtitle: {Path} (original kept at {Backup})", subtitleStream.Path, outcome.BackupPath);
            }
        }
        else
        {
            // EMBEDDED track: the subtitle was extracted earlier and synced to
            // tempOutput. The result is saved as a NEW external sidecar next
            // to the video. Video files are NEVER modified — no remuxing, no
            // container rewrite. Jellyfin discovers the sidecar via the
            // folder rescan below; the original embedded stream stays intact.
            job.Phase = "Saving synced subtitle";
            job.Progress = 0.75;

            var lang = string.IsNullOrWhiteSpace(subtitleStream.Language)
                ? null
                : subtitleStream.Language.Trim().ToLowerInvariant();
            // Dot-separated fields after the exact video filename, or Jellyfin will not
            // associate the sidecar with the episode and it never shows up.
            var target = lang is not null
                ? Path.Combine(videoDir, $"{videoNameNoExt}.SYNCED.{lang}.srt")
                : Path.Combine(videoDir, $"{videoNameNoExt}.SYNCED.srt");

            SyncedTargetNaming.RequireWritable(videoDir);
            File.Copy(tempOutput, target, overwrite: true);
            job.OutputPath = target;
            outcome.ChangedDir = videoDir;
            _logger.LogInformation(
                "Embedded subtitle synced as new external file: {Target} (video {Video} untouched)",
                target, videoPath);
        }

        // Step 4: Verify
        job.Phase = "Verifying";
        job.Progress = 0.95;

        if (string.IsNullOrEmpty(job.OutputPath) ||
            !File.Exists(job.OutputPath) ||
            new FileInfo(job.OutputPath).Length == 0)
        {
            // Whatever this job wrote is removed again if it does not hold subtitles: an empty sidecar
            // left in the library is reported by every later scan ("offline ... streams and format are
            // both null") and the user has no way to tell where it came from. The user's own file is
            // never touched here — replace mode has its own backup and rollback.
            if (!string.IsNullOrEmpty(job.OutputPath)
                && !string.Equals(job.OutputPath, subtitleStream.Path, StringComparison.Ordinal))
            {
                SafeDelete(job.OutputPath);
            }

            throw new InvalidOperationException("Subtitle verification failed — synced output is missing or empty.");
        }
    }

    /// <summary>
    /// What writing a synced subtitle records for the rest of the job: the folder the library monitor should be
    /// told about, and the backup of a replaced original that the failure path rolls back from.
    /// </summary>
    /// <remarks>
    /// It is filled as the write proceeds rather than returned, because the rollback needs the backup path exactly
    /// when the write throws. The first attempt at this extraction returned a tuple and lost both values on that
    /// path - the replace-mode original was then never restored from the kept backup. The P19 check caught it.
    /// </remarks>
    private sealed class SyncWriteOutcome
    {
        /// <summary>Gets or sets the folder the library monitor should be told about, if the job wrote one.</summary>
        public string? ChangedDir { get; set; }

        /// <summary>Gets or sets the kept original in replace mode, if there is one.</summary>
        public string? BackupPath { get; set; }
    }

    /// <summary>
    /// Builds the sentence a completed job carries: what changed (offset or rescale factor), a note when the
    /// subtitle looks like a signs track, and where a replaced original was kept. Also marks the job complete.
    /// </summary>
    /// <param name="job">The job, whose outcome, phase, status and progress this sets.</param>
    /// <param name="subtitleStream">The track being synced, for its path and whether it is external.</param>
    /// <param name="subtitleInputPath">The subtitle the user had, before any rescale.</param>
    /// <param name="engineInput">What the engine was actually given, when the input was rescaled.</param>
    /// <param name="backupPath">The kept original in replace mode, if there is one.</param>
    /// <param name="cuesNote">The signs/forced note, if the subtitle looked like one.</param>
    /// <param name="audioFallback">Whether a subtitle ruler was discarded and the audio used instead.</param>
    /// <param name="stretchDropped">Whether a framerate stretch was dropped after failing the audio check.</param>
    private void DescribeCompletedSync(
        SyncJob job,
        MediaBrowser.Model.Entities.MediaStream subtitleStream,
        string subtitleInputPath,
        string engineInput,
        string? backupPath,
        string? cuesNote,
        bool audioFallback,
        bool stretchDropped)
    {
        // Step 5: Success — describe what changed (offset ms / framerate). The replace-mode
        // backup is deliberately NOT deleted: a sync that succeeds while being wrong used to
        // leave the user with no way back, and the copy costs a few kilobytes. It is named
        // *.bak.subsync, which is not a subtitle extension, so Jellyfin never shows it as a
        // second track, and the result says where it is.
        var outcomeInput = backupPath ?? (subtitleStream.IsExternal ? subtitleStream.Path : subtitleInputPath);
        if (job.OutputPath is not null)
        {
            job.Outcome = AlignmentMetrics.DescribeSyncChange(outcomeInput, job.OutputPath);
            if (audioFallback)
            {
                job.Outcome = "the file's own subtitle track is not the same cut, so this was aligned against the "
                    + "audio" + (string.IsNullOrEmpty(job.Outcome) ? string.Empty : " \u00b7 " + job.Outcome);
            }
            else if (stretchDropped)
            {
                job.Outcome = "the stretch did not hold against the film's audio, so the subtitle was aligned with "
                    + "offsets only" + (string.IsNullOrEmpty(job.Outcome) ? string.Empty : " \u00b7 " + job.Outcome);
            }
            else if (engineInput != subtitleInputPath)
            {
                // The subtitle the user had and the corrected file are on differently scaled timelines, so
                // describing the difference between them reports about half the film's drift ("change=+55388 ms")
                // for a correction that did what it was asked to. Say what was done instead: the factor, and the
                // alignment's own change measured on the timeline the engine worked in.
                var before = AlignmentMetrics.ParseSrtCueStarts(subtitleInputPath);
                var after = AlignmentMetrics.ParseSrtCueStarts(engineInput);
                var factor = before is { Count: > 2 } && after is { Count: > 2 }
                    ? (after[^1] - after[0]) / (before[^1] - before[0])
                    : 1.0;
                var aligned = AlignmentMetrics.DescribeSyncChange(engineInput, job.OutputPath);
                job.Outcome = $"stretched to {factor:0.#####}x onto the reference's timeline"
                    + (string.IsNullOrEmpty(aligned) ? string.Empty : ", " + aligned);
            }
        }

        if (cuesNote is not null)
        {
            job.Outcome = string.IsNullOrEmpty(job.Outcome)
                ? cuesNote
                : job.Outcome + " \u00b7 " + cuesNote;
        }

        if (backupPath is not null)
        {
            // Worth saying plainly: the original file was overwritten in place.
            var kept = Path.GetFileName(backupPath);
            job.Outcome = string.IsNullOrEmpty(job.Outcome)
                ? "original replaced \u2014 kept at " + kept
                : job.Outcome + " \u00b7 original replaced, kept at " + kept;
            _logger.LogInformation("Original subtitle kept at {Backup} (replace mode)", backupPath);
        }

        job.Phase = "Complete";
        job.Status = SyncJobStatus.Completed;
        job.Progress = 1.0;
    }

    /// <summary>
    /// Announces a finished job: probes the size of what was written, logs the completion in both logs, tells the
    /// library monitor about the folder, and refreshes the item so its subtitle list is re-read. Every step is
    /// best-effort - the subtitle is already on disk, so a library hiccup must not turn a finished job into a
    /// failure, which is why this block does not sit inside the job's own try.
    /// </summary>
    /// <param name="job">The job that was written.</param>
    /// <param name="video">The item the subtitle belongs to.</param>
    /// <param name="changedDir">The folder to report, if the job wrote one.</param>
    private async Task AnnounceCompletedAsync(SyncJob job, Video video, string? changedDir)
    {
        // Check what is about to be announced. On a flaky share a write can disappear between
        // the copy and this line, and "completed" would then point at a file that is not there.
        long? outputSize = null;
        if (!string.IsNullOrEmpty(job.OutputPath))
        {
            try
            {
                var written = new FileInfo(job.OutputPath);
                if (written.Exists)
                {
                    outputSize = written.Length;
                }
            }
            catch (IOException)
            {
                // Unreadable size is not a failure; the existence check below decides.
            }
        }

        _logger.LogInformation(
            "Sync job {JobId} completed \u2014 wrote: {Output} ({Size}, {Outcome})",
            job.Id,
            job.OutputPath ?? "(no output path set)",
            outputSize is null ? "size unreadable" : $"{outputSize} bytes",
            job.Outcome ?? "unknown");

        LogPluginCompletion(job, outputSize);

        if (changedDir is not null && outputSize is null)
        {
            _logger.LogWarning(
                "The synced subtitle {Output} is not on disk, so the library was not told anything changed. Check the share and its permissions.",
                job.OutputPath ?? "(none)");
            changedDir = null;
        }

        // Tell Jellyfin about the new file, then refresh the item so its stream list is re-read.
        // Both are best-effort: the subtitle is already on disk, so a library hiccup must never
        // turn a finished job into a failure (this block used to sit in the job's own try, where
        // an exception marked the job FAILED and rolled the result back).
        //
        // The folder report is never skipped - it is what makes Jellyfin discover the file, and
        // Jellyfin coalesces repeats itself. The item refresh re-probes the media file, so it runs
        // once per item instead of once per subtitle track (see LibraryRefreshGate).
        if (changedDir is not null)
        {
            try
            {
                _libraryMonitor.ReportFileSystemChanged(changedDir);
                _logger.LogInformation("Reported the change to the Jellyfin library monitor: {Dir}", changedDir);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The library monitor rejected the change report for {Dir}; the subtitle is written and will appear after the next library scan", changedDir);
            }
        }

        if (_refreshGate.ShouldRefresh(video.Id))
        {
            try
            {
                await _libraryManager.UpdateItemAsync(
                    video,
                    video.GetParent(),
                    ItemUpdateType.MetadataImport,
                    CancellationToken.None).ConfigureAwait(false);
                _logger.LogInformation("Refreshed item {ItemId} so its subtitle list is re-read", video.Id);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Refreshing item {ItemId} failed; the subtitle is written and appears after the next scan", video.Id);
            }
        }
        else
        {
            _logger.LogDebug("Skipped the item refresh for {ItemId}: another subtitle of the same item was synced moments ago", video.Id);
        }
    }

    /// <summary>
    /// What resolving a job's reference decides, for everything downstream of it: the path the engine is handed,
    /// what that path is, and the speech-cache facts of the audio analysis this job either found or made.
    /// </summary>
    /// <remarks>
    /// It exists because the audio-reference builder is called from four places - two inside reference resolution
    /// and two outside it (the wrong-cut fallback and the suspicious-reference cross-check) - and because the job's
    /// <c>finally</c> reads <see cref="SpeechKey"/> and <see cref="SerializeSpeech"/> to drop the analysis link.
    /// A closure could reach those locals; a method cannot, so they travel in an object the caller owns and
    /// declares before the job's <c>try</c>.
    /// </remarks>
    private sealed class ReferenceResolution
    {
        /// <summary>Gets or sets the path handed to the engine as its reference. Never the media file itself
        /// (S11): the container is never handed over, because the engine would demux the whole thing itself.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>Gets or sets what that reference is: a stream spec such as <c>s:0</c>, or null when the
        /// reference is the file's own audio.</summary>
        public string? Spec { get; set; }

        /// <summary>Gets or sets the engine's reference-stream argument, when the reference is a stream.</summary>
        public string? Stream { get; set; }

        /// <summary>Gets or sets a value indicating whether the reference is a sibling subtitle this run built or
        /// reused, rather than the audio.</summary>
        public bool UsedSubtitleReference { get; set; }

        /// <summary>Gets or sets the speech-cache key of the analysis this job found or produced, if any.</summary>
        public string? SpeechKey { get; set; }

        /// <summary>Gets or sets a value indicating whether this job's run writes the analysis for others to
        /// reuse.</summary>
        public bool SerializeSpeech { get; set; }

        /// <summary>Gets or sets a value indicating whether the engine is given a stored analysis rather than the
        /// media file.</summary>
        public bool UsingCachedSpeech { get; set; }
    }

    /// <summary>
    /// Prepares the file's own audio as the job's reference: reuses the stored analysis when one exists, otherwise
    /// waits for the file's gate and does the analysis this job's run will produce.
    /// </summary>
    /// <param name="reference">The resolution being built; the speech-cache facts are set here.</param>
    /// <param name="job">The job, for its phase label and the speech gate marker.</param>
    /// <param name="videoPath">The media file the analysis belongs to.</param>
    /// <param name="why">Why the audio is being used, for the log line.</param>
    /// <param name="cancellationToken">Cancels the wait for the file's gate.</param>
    /// <returns>The path the engine should be given.</returns>
    private async Task<string> PrepareAudioReferenceAsync(
        ReferenceResolution reference,
        SyncJob job,
        string videoPath,
        string why,
        CancellationToken cancellationToken)
    {
        {
            // Identity of everything that shapes the speech signal: the binary in use,
            // the bundled engine version and this plugin's own version. A path alone is
            // not enough — an upgraded bundled binary keeps its path.
            var engineIdentity = string.Join(
                "|",
                ResolveFfSubSyncPath(),
                _engine.BundledFfSubSyncVersion,
                typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
            // The analysis this key names is the one the engine will produce, so the key names the VAD the
            // engine is actually given for the audio path - not the configured one, which it is not given.
            reference.SpeechKey = SpeechCache.KeyFor(
                videoPath,
                AudioReferenceVad + "|audio",
                engineIdentity);
            var cached = SpeechCache.TryGet(reference.SpeechKey);
            if (cached is not null)
            {
                reference.UsingCachedSpeech = true;
                job.Phase = SyncPhaseLabel(fromCache: true, audioReference: true);
                _logger.LogInformation("Reusing the stored audio analysis for {Video} ({Why})", videoPath, why);
                PluginLog.Info($"[{job.Id}] reference: method=speech-cache why={why}");
                return cached;
            }

            // The analysis is per *file*, not per subtitle: hold the file's gate so the first job does
            // it and the rest reuse the harvest. They wait here rather than starting a second analysis.
            var speechGate = _speechGates.GetOrAdd(videoPath, _ => new SemaphoreSlim(1, 1));

            // Cancellable, and that is the point: this wait can last as long as the file's audio analysis
            // (a feature film's is over an hour), so a job parked here has to be reachable by the user's
            // Kill and by the stall watchdog. Without the token neither could end it, and the job held its
            // worker slot until the server was restarted (B6).
            await speechGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            job.HoldsSpeechGate = true;

            var harvestedWhileWaiting = SpeechCache.TryGet(reference.SpeechKey);
            if (harvestedWhileWaiting is not null)
            {
                speechGate.Release();
                job.HoldsSpeechGate = false;
                reference.UsingCachedSpeech = true;
                job.Phase = SyncPhaseLabel(fromCache: true, audioReference: true);
                _logger.LogInformation("Reusing the audio analysis another job stored for {Video} ({Why})", videoPath, why);
                PluginLog.Info($"[{job.Id}] reference: method=speech-cache why={why} (harvested by another job of this file while this one waited)");
                return harvestedWhileWaiting;
            }

            reference.SerializeSpeech = true;
            job.Phase = SyncPhaseLabel(fromCache: false, audioReference: true);
            PluginLog.Info($"[{job.Id}] reference: method=audio why={why} (this job does the file's analysis; the others wait for it)");
            return SpeechCache.CreateReferenceLink(videoPath, reference.SpeechKey);
        }
    }

    /// <summary>
    /// Resolves what this job is aligned against: the file's own audio (analysed once and reused through the speech
    /// cache) or a sibling subtitle track built here from text this process already has. The container itself is
    /// never handed to the engine (S11) - that makes it demux the whole file, once per job.
    /// </summary>
    /// <param name="reference">The resolution to fill in.</param>
    /// <param name="job">The job, for its phase label and the extraction/language context.</param>
    /// <param name="videoPath">The media file.</param>
    /// <param name="videoDurationForReference">The file's duration, used to spot a signs/forced track.</param>
    /// <param name="cancellationToken">Cancels the per-file waits.</param>
    private async Task ResolveReferenceAsync(
        ReferenceResolution reference,
        SyncJob job,
        string videoPath,
        TimeSpan videoDurationForReference,
        CancellationToken cancellationToken)
    {
        // The reference this job is aligned against is one of exactly two things: the file's own
        // audio (analysed once, reused through the speech cache) or a sibling subtitle track that
        // our own reader produced. What it must never be is the media file itself: passing the
        // container to ffsubsync makes the engine demux the whole thing with its own ffmpeg, once
        // per job. Measured on this fixture — a 2.38 GB episode — two jobs sat in that demux for 7
        // and 17 minutes and never finished, and on a bulk run that is what stops the batch ever
        // reaching the end (S11).
        var usesAudioReference = reference.Stream is null
            || reference.Stream.StartsWith("a:", StringComparison.Ordinal);

        // The speech signal depends on the media file, the VAD method and the engine
        // build — never on the subtitle, its language or the mode. Analysing it is work
        // that happens anyway, so it is always kept: the other subtitles of that file and
        // any later run then skip the audio pass entirely. This is also the fallback whenever a
        // subtitle reference cannot be built — the plugin never refuses a job, it changes the
        // ruler it measures against.
        if (usesAudioReference)
        {
            reference.Path = await PrepareAudioReferenceAsync(reference, job, videoPath, "the audio is this job's own reference", cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // The reference is a sibling subtitle track, and it is built here from text this
            // process already has. Letting ffsubsync pull the stream out of the video instead is
            // not: it demuxes the whole file. Measured on an 8.2 GB episode: 12.5 s and 8218 MB
            // read, repeated for every subtitle.
            var referenceIdentity = string.Join(
                "|",
                ResolveFfSubSyncPath(),
                _engine.BundledFfSubSyncVersion,
                typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
            var referenceOrdinal = MediaStreamMap.SubtitleStreamOrdinal(reference.Stream);
            reference.Spec = reference.Stream;

            // The reference lives in this run's own directory and is shared with the other
            // subtitles of this file while they are still going to use it. It is deliberately
            // never carried over from an earlier run: a reference taken from a sibling subtitle
            // inherits that track's own error, and every other track of the file then inherits it
            // in turn.
            var referenceTarget = referenceOrdinal >= 0
                ? ReferenceStore.Reserve(videoPath, reference.Stream!, referenceIdentity)
                : null;

            // One job at a time builds this file's reference; the ones that follow reuse the file
            // it wrote. Two builders used to race on one shared "<target>.part", and the loser
            // either failed to write it or failed to move it into place — a race that ended in
            // "let ffsubsync demux the file instead", the very thing this block exists to avoid.
            var referenceGate = _referenceGates.GetOrAdd(videoPath, _ => new SemaphoreSlim(1, 1));
            string? referenceWhy = null;

            if (referenceTarget is not null)
            {
                await referenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    if (File.Exists(referenceTarget))
                    {
                        var reuseCues = ReferenceStore.CueCount(referenceTarget);
                        if (AlignmentMetrics.LooksLikeSignsTrack(reuseCues, videoDurationForReference))
                        {
                            // A reference with a handful of cues over a whole episode is a
                            // signs/forced track: it cannot align anything. Take it out of the
                            // running and let this job use the audio instead - the sync still
                            // happens, it just stops being built on a bad ruler.
                            _logger.LogWarning(
                                "The reference subtitle {Track} for {Video} holds only {Cues} cue(s) - a signs track, not usable as a reference; syncing against the audio instead",
                                reference.Spec,
                                videoPath,
                                reuseCues);
                            PluginLog.Info(
                                $"[{job.Id}] reference {reference.Spec} has only {reuseCues} cue(s) (a signs/forced track), "
                                + "so it is not usable as a ruler - falling back to the audio for this job");
                            ReferenceStore.Discard(videoPath, reference.Spec!);
                            referenceTarget = null;
                            referenceWhy = $"only {reuseCues} cue(s): a signs/forced track";
                        }
                        else
                        {
                            reference.Path = referenceTarget;
                            reference.Stream = null;
                            reference.UsedSubtitleReference = true;
                            job.Phase = SyncPhaseLabel(fromCache: true, audioReference: false);
                            _logger.LogInformation(
                                "Reusing this run's reference subtitle for {Video}: track {Reference}, {Cues} cues",
                                videoPath,
                                reference.Spec,
                                reuseCues);
                        }
                    }

                    if (referenceTarget is not null && !File.Exists(referenceTarget))
                    {
                        // The track's text, taken from wherever it already is: this run's memory,
                        // the extracted-subtitle cache, or the container's own index. Never a
                        // whole-file ffmpeg read — that is the cost this whole block exists to
                        // avoid.
                        var referenceText = await TryReadReferenceTextAsync(
                            videoPath, referenceOrdinal, job, cancellationToken).ConfigureAwait(false);

                        if (string.IsNullOrWhiteSpace(referenceText))
                        {
                            referenceWhy = "the track's text could not be read from the container index";
                        }
                        else
                        {
                            // Written under a name of this job's own and moved into place: the
                            // reference is handed to ffsubsync as a path, and a reader must never
                            // see a half-written file.
                            var referencePart = referenceTarget + "." + job.Id + ".part";
                            try
                            {
                                Directory.CreateDirectory(Path.GetDirectoryName(referenceTarget)!);
                                await File.WriteAllTextAsync(
                                    referencePart, referenceText, new System.Text.UTF8Encoding(false), cancellationToken)
                                    .ConfigureAwait(false);
                                File.Move(referencePart, referenceTarget, overwrite: true);
                                ReferenceStore.MarkReady(videoPath);
                                reference.Path = referenceTarget;
                                reference.Stream = null;
                                reference.UsedSubtitleReference = true;
                                job.Phase = SyncPhaseLabel(fromCache: false, audioReference: false);
                                _logger.LogInformation(
                                    "Built this run's reference subtitle for {Video} from track {Reference}: {Cues} cues (deleted once this file's subtitles are done)",
                                    videoPath,
                                    reference.Spec,
                                    SrtWriter.CountCues(referenceText));
                                PluginLog.Info(
                                    $"[{job.Id}] reference: method=subtitle cues={SrtWriter.CountCues(referenceText)} "
                                    + $"track={reference.Spec} file={videoPath}");
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                try { File.Delete(referencePart); } catch (IOException) { /* best effort */ }
                                referenceWhy = ex.Message;
                                _logger.LogWarning(ex, "Could not write the reference subtitle for {Video}", videoPath);
                            }
                        }
                    }
                }
                finally
                {
                    referenceGate.Release();
                }
            }

            if (!reference.UsedSubtitleReference)
            {
                // No usable subtitle reference for this job. The audio is analysed instead —
                // through the speech cache, so that a file's other subtitles pay for it once —
                // and the container is never handed over: that demux is what left a bulk run
                // unable to finish.
                PluginLog.Info(
                    $"[{job.Id}] reference {reference.Spec ?? "(none)"} unusable ({referenceWhy ?? "not available"}) "
                    + "- aligning against the audio instead");
                _logger.LogWarning(
                    "No usable reference subtitle for {Video} ({Why}); syncing against the audio instead",
                    videoPath,
                    referenceWhy ?? "not available");
                reference.Spec = null;
                reference.Stream = null;
                reference.Path = await PrepareAudioReferenceAsync(
                    reference, job, videoPath,
                    "no reference subtitle could be built for this job", cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Ends a job the user killed: the cancellation is the outcome, not a failure, and the temp directory is
    /// removed by the cleanup that follows every run.
    /// </summary>
    /// <param name="job">The job that was killed.</param>
    private void MarkCancelled(SyncJob job)
    {
            _logger.LogInformation("Sync job {JobId} was killed by the user", job.Id);
            job.Status = SyncJobStatus.Cancelled;
            job.Phase = "Killed";
            job.Error = "Killed by the user.";
            job.FinishedAtUtc = DateTime.UtcNow;
    }

    /// <summary>
    /// Ends a job that threw, after undoing an in-place replace. The backup is kept when the rollback itself
    /// fails - it is the user's last resort - and the take is taken from <paramref name="backupPath"/> rather than
    /// from the write step's return value, because a write that throws returns nothing.
    /// </summary>
    /// <param name="job">The job that failed.</param>
    /// <param name="ex">What it failed with.</param>
    /// <param name="backupPath">The kept original of a replaced subtitle, if any.</param>
    private void FailJobAndRollBack(SyncJob job, Exception ex, string? backupPath)
    {
            _logger.LogError(ex, "Subtitle sync failed for job {JobId}", job.Id);
            PluginLog.Error($"job {job.Id} failed: mode={job.Mode} item={job.ItemId} stream={job.SubtitleIndex}", ex);

            // ROLLBACK: if we created a backup but didn't complete successfully,
            // restore the original file from backup
            if (backupPath is not null && File.Exists(backupPath))
            {
                try
                {
                    // Determine what the original file was
                    var originalPath = backupPath.Substring(0, backupPath.Length - ".bak.subsync".Length);
                    _logger.LogWarning("Rolling back: restoring {Original} from backup {Backup}", originalPath, backupPath);
                    File.Copy(backupPath, originalPath, overwrite: true);
                    SafeDelete(backupPath);
                    _logger.LogInformation("Rollback complete: {Original} restored", originalPath);
                }
                catch (Exception rollbackEx)
                {
                    _logger.LogError(rollbackEx, "ROLLBACK FAILED for job {JobId}! Backup file preserved at {Backup}", job.Id, backupPath);
                    // Do NOT delete the backup — it's the user's last resort
                }
            }

            job.Status = SyncJobStatus.Failed;
            job.Error = ex.Message;
    }

    /// <summary>
    /// Releases everything a finished job held, in the order it was taken: the temp directory, the file's speech
    /// gate, the audio-analysis link (S46 - it has to outlive every run of this job, not just the first), and the
    /// shared extraction tree, which goes away only when the last job reading it is done. Every step is
    /// best-effort: a cleanup that fails must not change a job that already finished.
    /// </summary>
    /// <param name="job">The job that finished.</param>
    /// <param name="video">Its media item, for the per-file gate and the shared tree.</param>
    /// <param name="tempDir">The job's temp directory.</param>
    /// <param name="serializeSpeech">Whether this job's own analysis produced the cached speech.</param>
    /// <param name="speechKey">The speech-cache key of that analysis, if there is one.</param>
    private void CleanUpAfterJob(SyncJob job, Video video, string tempDir, bool serializeSpeech, string? speechKey)
    {
            // Clean up temp directory (contains ffsubsync output, extracted subs, etc.)
            try
            {
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
            catch
            {
                // Non-critical
            }

            // The shared extraction directory goes away only when the last job reading it is done; this job
            // deleting it is exactly what used to fail the jobs that came after it.
            try
            {
                ReleaseSpeechGate(job, video.Path);
            }
            catch
            {
                // Non-critical: the next job of this file will do its own analysis.
            }

            // The audio-analysis symlink this job created outlives its engine runs and goes away here, with
            // the job that made it (S46). Dropping it as soon as the first run finished left the
            // wider-window retry and the verification run of the *same* job asking the engine for a file that
            // no longer existed, so a job whose answer had reached the search window refused instead of
            // being rescued: 7 of the 8 refusals in one field run were exactly this, all of them naming a
            // path under speech-cache/ that the plugin itself had deleted. The harvested .npz stays - that
            // is the artefact worth keeping - and Prune() only ever touches .npz and .ref.srt, so a link
            // that outlives its job is still cleared by the next prune or by the next job's own link.
            try
            {
                if (speechKey is not null && serializeSpeech)
                {
                    SpeechCache.DropLink(speechKey);
                }
            }
            catch
            {
                // Non-critical: the link is a temp artefact and the next prune clears it.
            }

            try
            {
                SharedExtractionStore.Release(video.Path, job.Id);
            }
            catch
            {
                // Non-critical: the next cleanup clears it.
            }
    }

    /// <summary>
    /// Ends a job as refused: nothing was written, so the output path is cleared and the engine's temporary output
    /// is removed.
    /// </summary>
    /// <remarks>
    /// Every refusal in a job's run goes through here. Before the extraction each of the five refusal sites wrote
    /// the same seven fields itself, and they had already drifted: one of them (the wrong-cut reference) left
    /// <c>FinishedAtUtc</c> set by the same lines as the others while another relied on the caller. The wording
    /// stays at the site, because it is built from the values that decided the refusal.
    /// </remarks>
    /// <param name="job">The job being refused.</param>
    /// <param name="phase">The phase to leave on the job.</param>
    /// <param name="error">The sentence the user is shown.</param>
    /// <param name="tempOutput">The engine's output in the job's temp directory, if any.</param>
    private void RefuseJob(SyncJob job, string phase, string error, string? tempOutput)
    {
        job.Status = SyncJobStatus.Failed;
        job.Phase = phase;
        job.Error = error;
        job.Progress = 1.0;
        job.FinishedAtUtc = DateTime.UtcNow;
        job.OutputPath = null;
        SafeDelete(tempOutput);
    }

    /// <summary>
    /// Ends a job whose run wrote no subtitle at all because the engine suppressed its write: the subtitle is
    /// already in sync, so this is a success with nothing to write (P7's outcome).
    /// </summary>
    /// <param name="job">The job that finished.</param>
    /// <param name="cuesNote">The signs-track note, when there is one.</param>
    private void CompleteAlreadyInSync(SyncJob job, string? cuesNote)
    {
        job.Outcome = "already in sync (shift under 3 s) \u2014 no change needed"
            + (cuesNote is null ? string.Empty : " \u00b7 " + cuesNote);
        job.Phase = "Complete";
        job.Status = SyncJobStatus.Completed;
        job.Progress = 1.0;
        _logger.LogInformation("Sync job {JobId}: subtitle already in sync \u2014 no output written", job.Id);
        LogPluginCompletion(job, null);
    }

    /// <summary>
    /// Ends a job whose engine output was measurably the same as its input: nothing changed for the user, so
    /// nothing is written (P12's outcome).
    /// </summary>
    /// <param name="job">The job that finished.</param>
    /// <param name="noChange">The measurement that says nothing moved.</param>
    /// <param name="cuesNote">The signs-track note, when there is one.</param>
    /// <param name="tempOutput">The engine's output, which is removed.</param>
    private void CompleteAsNoChange(SyncJob job, AlignmentMetrics.SyncChange noChange, string? cuesNote, string? tempOutput)
    {
        _logger.LogInformation(
            "Sync job {JobId}: the sync changed nothing ({Change}) \u2014 no sidecar written",
            job.Id,
            noChange.Describe());
        job.Outcome = $"already in sync ({noChange.Describe()}) \u2014 nothing written"
            + (cuesNote is null ? string.Empty : " \u00b7 " + cuesNote);
        job.Phase = "Complete";
        job.Status = SyncJobStatus.Completed;
        job.Progress = 1.0;
        job.FinishedAtUtc = DateTime.UtcNow;
        job.OutputPath = null;
        SafeDelete(tempOutput);
        LogPluginCompletion(job, null);
    }

    /// <summary>
    /// Whether the engine's own words say it could not read the reference it was handed (S22).
    /// </summary>
    /// <remarks>
    /// A run that failed to open its reference is not a search-window problem, and refusing with "raise Maximum
    /// offset" sends the user to a setting that cannot help: measured in the field on 2026-09-15, 7 of the 8
    /// refusals in one run were this shape, every one of them naming a path under <c>state/speech-cache</c> that the
    /// engine could not open. The markers are the engine's own words, taken from that log.
    /// </remarks>
    /// <param name="engineTail">The tail of what the engine printed, as the refusal message already carries it.</param>
    /// <returns>True when the engine said it could not read the reference.</returns>
    internal static bool EngineCouldNotReadReference(string engineTail)
        => engineTail.Contains("unable to read reference", StringComparison.OrdinalIgnoreCase)
        || engineTail.Contains("No such file or directory", StringComparison.OrdinalIgnoreCase)
        || engineTail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Thrown when the work a request asks for is already queued by a different account (F4).
    /// </summary>
    /// <remarks>
    /// Distinct from a plain refusal so the endpoints can answer 409 rather than 400: nothing about the request is
    /// wrong, the queue simply holds that work for somebody else.
    /// </remarks>
    public sealed class SyncQueueConflictException : InvalidOperationException
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="SyncQueueConflictException"/> class.
        /// </summary>
        /// <param name="message">Why the request conflicts with the queue.</param>
        public SyncQueueConflictException(string message)
            : base(message)
        {
        }
    }

    /// <summary>How long a teardown waits for the lanes and the pump to leave (B14).</summary>
    internal const int ShutdownWaitMs = 5000;

    /// <summary>
    /// Waits for a set of tasks, for a bounded time, and reports how many finished (B14).
    /// </summary>
    /// <param name="tasks">The tasks to wait for.</param>
    /// <param name="milliseconds">Deadline in milliseconds.</param>
    /// <returns>How many of those tasks had completed by the deadline.</returns>
    internal static int WaitForTasks(IEnumerable<Task> tasks, int milliseconds)
    {
        var list = tasks.ToList();
        if (list.Count == 0)
        {
            return 0;
        }

        try
        {
            Task.WaitAll(list.ToArray(), milliseconds);
        }
        catch (AggregateException)
        {
            // A task that faulted is finished either way; the count below says how many got there.
        }

        return list.Count(task => task.IsCompleted);
    }

    /// <summary>How long a finished job's row stays in the interface.</summary>
    private static readonly TimeSpan JobRetention = TimeSpan.FromHours(1);

    /// <summary>
    /// Most finished jobs kept before the oldest are dropped, however young they are.
    /// </summary>
    /// <remarks>
    /// A backstop against an unbounded dictionary, and it has to sit above what a real run produces: the
    /// previous rule dropped every completed job as soon as the store passed 50 entries, so a 2 497-task batch
    /// lost the beginning of its own history while the user was watching it (B17). 10 000 rows cost a few
    /// megabytes and no realistic batch reaches them.
    /// </remarks>
    private const int MaxTrackedJobs = 10000;

    /// <summary>
    /// Safely replaces an external subtitle file with the synced version.
    /// Creates a backup first, then atomically renames the new file into place.
    /// </summary>
    private async Task ReplaceExternalSubtitle(string originalPath, string backupPath, string syncedTempPath)
    {
        if (!File.Exists(originalPath))
        {
            throw new FileNotFoundException($"Original subtitle file not found: {originalPath}");
        }

        // 1. Copy original → backup (preserves original permissions/attrs)
        _logger.LogInformation("Backing up original subtitle: {Original} → {Backup}", originalPath, backupPath);
        File.Copy(originalPath, backupPath, overwrite: false);

        // 2. Copy synced temp → original (use Copy+Delete instead of cross-device Rename)
        _logger.LogInformation("Replacing subtitle with synced version: {Temp} → {Original}", syncedTempPath, originalPath);
        await Task.Run(() => File.Copy(syncedTempPath, originalPath, overwrite: true)).ConfigureAwait(false);
    }

    /// <summary>
    /// Picks the path a replace run keeps the user's original subtitle at.
    ///
    /// A name ending in <c>.bak.subsync</c> is deliberately not a subtitle extension, so Jellyfin
    /// never offers the backup as a second track. An earlier run's backup is never overwritten:
    /// each replace keeps the copy it made, so the chain of originals stays intact.
    /// </summary>
    /// <param name="originalPath">The subtitle that is about to be overwritten.</param>
    /// <returns>A path that does not exist yet.</returns>
    private static string NextBackupPath(string originalPath)
    {
        var first = originalPath + ".bak.subsync";
        if (!File.Exists(first))
        {
            return first;
        }

        for (var n = 2; n < 1000; n++)
        {
            var candidate = originalPath + ".bak" + n.ToString(CultureInfo.InvariantCulture) + ".subsync";
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }

        return originalPath + ".bak." + Guid.NewGuid().ToString("N").Substring(0, 8) + ".subsync";
    }

    /// <summary>
    /// Deletes a file if it exists. Swallows all exceptions.
    /// </summary>
    private static void SafeDelete(string? path)
    {
        if (path is null) return;
        try { if (File.Exists(path)) File.Delete(path); } catch { /* non-critical */ }
    }

    /// <summary>
    /// Regex to extract tqdm percentage from ffsubsync stderr.
    /// Matches patterns like " 42%|..." at the start of a line.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex TqdmPercentRegex =
        new(@"^\s*(\d+)%\|", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Parses a single stderr line from ffsubsync and updates job progress/phase.
    /// ffsubsync outputs tqdm progress bars and phase log lines.
    /// Progress mapping: speech extraction 10-55%, subtitle extraction 55-60%, alignment 60-75%.
    /// </summary>
    private void ParseFfSubSyncStderr(string line, SyncJob job)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        // Try to parse tqdm percentage
        var match = TqdmPercentRegex.Match(line);
        if (match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var percent))
        {
            // Speech extraction phase: map 0-100% → 0.10-0.55
            job.Progress = 0.10 + (percent / 100.0) * 0.45;
            job.Phase = "Analyzing speech";
            return;
        }

        // Check for phase messages in log lines (lowercase to match stderr format)
        var lower = line.ToLowerInvariant();

        if (lower.Contains("extracting speech segments from subtitle"))
        {
            job.Phase = "Extracting subtitle speech";
            job.Progress = 0.55;
        }
        else if (lower.Contains("computing alignments"))
        {
            job.Phase = "Computing alignment";
            job.Progress = 0.60;
        }
        else if (lower.Contains("got score") && lower.Contains("for ratio"))
        {
            // Individual alignment iterations — nudge progress 0.60 → 0.75
            // Each iteration is ~1s; we just slowly creep up
            job.Phase = "Computing alignment";
            job.Progress = Math.Min(job.Progress + 0.01, 0.74);
        }
        else if (lower.Contains("writing output"))
        {
            job.Phase = "Writing output";
            job.Progress = 0.75;
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        _disposing = true;

        // The sweep's records are written in batches, so the last few are still in memory here. Shutdown is
        // the last guaranteed point to get them on disk.
        try
        {
            if (_sweepState.IsValueCreated)
            {
                _sweepState.Value.Flush();
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to flush the sweep state on shutdown");
        }

        // Last write wins on shutdown, so the history a restart comes back to is the one just left.
        BatchHistory.Save(BatchHistory.DefaultPath, SnapshotBatchHistory());

        // A pass in flight is reading the media share; teardown must not wait for it forever - but it does have to
        // stop it (B14). Cancelling the lane token only asks, and until this block existed nothing killed the child
        // processes or waited for a lane at all, so a plugin update or a server restart with jobs in flight left
        // ffmpeg and ffsubsync children reading the share for a service that no longer existed.
        try { _laneStop.Cancel(); }
        catch (ObjectDisposedException) { /* already cancelled */ }

        // Read before anything is cancelled: cancelling a run token fires the runner's own kill callback, and the
        // runner then removes its entry, so a count taken later would say "nothing was running" about a run that was.
        var trackedAtStart = _processes.TrackedCount;

        foreach (var entry in _jobCancellation)
        {
            try { entry.Value.Cancel(); }
            catch (ObjectDisposedException) { /* the run finished first */ }
        }

        var (childrenAsked, childrenStopped) = _processes.KillChildProcesses();
        var tasksWaited = _laneTasks.Count + (_pumpTask is null ? 0 : 1);
        var tasksDone = WaitForLanes(ShutdownWaitMs);
        var trackedLeft = JobProcessRegistry.LiveProcesses;
        var childrenLeft = _processes.LiveCount();

        // What the teardown can honestly claim: how many children it knew about, how many of those are not alive now,
        // and how many it had to kill itself. The rest were stopped by their own runner's cancellation callback, which
        // is the normal path for a run in flight - reporting only the direct kills would say "0 stopped" about a
        // teardown that stopped two.
        var childrenGone = trackedAtStart - childrenLeft;

        // The registry is process-wide and outlives this instance, so a reloaded plugin must not inherit entries for
        // jobs that ended with the old one.
        JobProcessRegistry.Clear();

        _logger.LogInformation(
            "SubSync teardown: {Started} process(es) tracked, {Gone} stopped ({Killed} killed here), {Left} alive, {Done}/{Tasks} lane(s) and pump finished",
            trackedAtStart, childrenGone, childrenAsked, childrenLeft, tasksDone, tasksWaited);
        PluginLog.Info(
            $"teardown: tracked={trackedAtStart} stopped={childrenGone} killedDirect={childrenAsked} "
            + $"exitedDirect={childrenStopped} aliveAfter={childrenLeft} "
            + $"tasksDone={tasksDone}/{tasksWaited} trackedLeft={trackedLeft}");

        try { _cleanupTimer.Dispose(); }
        catch { /* already disposed */ }

        // Unblock a parked pump so it can observe _disposing and exit, then
        // release the semaphore. Registered as a DI singleton, Jellyfin calls
        // this once at shutdown.
        try { _wakePump.Release(); }
        catch { /* pump not parked or already released */ }

        try { _extractWake.Release(); }
        catch { /* lane not parked or already released */ }

        try { _wakePump.Dispose(); }
        catch { /* already disposed */ }
    }

    /// <summary>
    /// Sweeps the stores that outlive a job, so a long run cannot accumulate them (B12).
    /// </summary>
    /// <remarks>
    /// Four stores grow with what a run has touched: the extracted-subtitle cache (bounded in memory since
    /// B12), the reference store's per-file entries, the shared extraction directories, and the sweep state's
    /// records. Each is removed by the job that created it - the last job of a file releases its reference and
    /// its shared directory - and each is left behind by a job that ends without reaching that call: a task
    /// that never unwound after a kill or a stall stop, a job context evicted while its reference was
    /// reserved, a subtitle file replaced between two runs. Nothing removed those until the next plugin start,
    /// which on a server left running for weeks is not a bound at all.
    /// <para>
    /// Liveness is read from the job table, not from the stores: a file is still being worked on when any of
    /// its jobs is queued or running, and ffsubsync reading a reference happens inside a running job, so a
    /// reference is never removed from under the engine.
    /// </para>
    /// </remarks>
    private void SweepLongLivedStores()
    {
        try
        {
            var now = DateTime.UtcNow;
            var last = new DateTime(Interlocked.Read(ref _lastStoreSweepTicks), DateTimeKind.Utc);
            if (now - last < TimeSpan.FromMinutes(StoreSweepMinutes))
            {
                return;
            }

            Interlocked.Exchange(ref _lastStoreSweepTicks, now.Ticks);

            // Scratch directories are the other thing a run leaves behind (F7): a job that was killed or whose process
            // died never reached its own cleanup, and its directory then sat in the cache root until an operator pressed
            // "Clear cache". They are swept on the same pass that sweeps the stores, and once when the plugin loads.
            var scratchRemoved = ClearStaleJobDirectories();
            if (scratchRemoved > 0)
            {
                PluginLog.Info($"sweep: removed {scratchRemoved} orphaned job scratch director(ies)");
            }

            var liveFiles = new HashSet<string>(StringComparer.Ordinal);
            foreach (var job in _jobs.Values)
            {
                if (job.Status is not (SyncJobStatus.Queued or SyncJobStatus.Running))
                {
                    continue;
                }

                if (_jobContexts.TryGetValue(job.Id, out var ctx) && ctx.Video?.Path is { } path)
                {
                    liveFiles.Add(path);
                }
            }

            var agedSubtitles = SubtitleCache.Prune();
            var orphanedReferences = ReferenceStore.SweepOrphans(liveFiles.Contains);
            var orphanedShared = SharedExtractionStore.Cleanup(
                id => _jobs.TryGetValue(id, out var live)
                    && live.Status is SyncJobStatus.Queued or SyncJobStatus.Running);

            if (agedSubtitles > 0 || orphanedReferences > 0 || orphanedShared > 0)
            {
                PluginLog.Info(
                    $"stores: subtitleCache dropped={agedSubtitles} cached={SubtitleCache.MemoryCount} entries/"
                    + $"{SubtitleCache.MemoryChars} chars kept, references={orphanedReferences} orphan(s) removed, "
                    + $"sharedDirs={orphanedShared} orphan(s) removed, liveFiles={liveFiles.Count}");
                _logger.LogInformation(
                    "Store sweep: {Subtitles} subtitle-cache entries dropped, {References} orphaned references and "
                    + "{Shared} shared extraction directories removed",
                    agedSubtitles, orphanedReferences, orphanedShared);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error while sweeping the long-lived stores");
        }
    }

    /// <summary>
    /// Stops jobs that have held their worker slot without making progress, so a run can finish (B6).
    /// </summary>
    /// <remarks>
    /// A job that is stuck does not just waste a slot: the batch it belongs to counts it as unfinished, the
    /// interface shows a run that never ends, and the only way out was to restart Jellyfin. The decision is
    /// made by <see cref="StuckJobPolicy" /> from what was observed - the job's own activity, whether a child
    /// process is running for it, how long that process has been silent, and whether another subtitle of the
    /// same file is working - and the numbers are the user's own settings
    /// (<c>StuckJobTimeoutMinutes</c>, <c>WedgedProcessTimeoutMinutes</c>).
    /// <para>
    /// The job is failed rather than cancelled, and it is failed <i>before</i> its token is cancelled: the
    /// cancellation handler would otherwise relabel it "Cancelled by user", which is an action nobody took, and
    /// the cancel is what unwinds whatever the job was waiting in so its slot comes back.
    /// </para>
    /// </remarks>
    private void ReapStuckJobs()
    {
        try
        {
            var now = DateTime.UtcNow;
            var last = new DateTime(Interlocked.Read(ref _lastReapTicks), DateTimeKind.Utc);
            if (now - last < TimeSpan.FromSeconds(ReapIntervalSeconds))
            {
                return;
            }

            Interlocked.Exchange(ref _lastReapTicks, now.Ticks);

            var windows = StuckJobWindows.From(Services.SettingsSource.Current());
            var observations = _jobs.Values
                .Select(job => new JobObservation(
                    job,
                    _jobContexts.TryGetValue(job.Id, out var ctx) ? ctx.Video?.Path : null,
                    job.LastActivityUtc,
                    JobProcessRegistry.IsAlive(job.Id),
                    JobProcessRegistry.SilentSinceUtc(job.Id)))
                .ToList();

            var stuckJobs = StuckJobPolicy.Stuck(observations, now, windows);

            foreach (var (job, reason) in stuckJobs)
            {
                StuckJobPolicy.Stop(job, reason, now);

                var hadToken = _jobCancellation.TryGetValue(job.Id, out var cts);
                if (hadToken)
                {
                    try
                    {
                        cts!.Cancel();
                    }
                    catch (ObjectDisposedException)
                    {
                        // The job had already finished; nothing to stop.
                    }
                }

                PluginLog.Warn(
                    $"job {job.Id} stopped: {reason} (mode={job.Mode} batch={job.BatchId ?? "(standalone)"} "
                    + $"item={job.ItemId} stream={job.SubtitleIndex} stopped={hadToken})");
                _logger.LogWarning("Sync job {JobId} stopped: {Reason}", job.Id, reason);
            }

            if (stuckJobs.Count > 0)
            {
                // A stopped job frees its slot as soon as it unwinds, and the next queued task may already be
                // startable - the planner has to look, since the stop did not come from a job finishing.
                WakePump();
            }
        }
        catch (Exception ex)
        {
            // Never let the watchdog be the thing that breaks a run.
            _logger.LogDebug(ex, "Error while checking jobs for progress");
        }
    }

    /// <summary>
    /// Evicts completed and failed jobs from the in-memory store to prevent memory leaks.
    /// </summary>
    private void CleanupOldJobs()
    {
        try
        {
            // Recovery, not just housekeeping: this timer runs whether or not the scheduler is working, so a
            // job that stopped making progress is stopped even if the pump itself is wedged (B6, B31).
            ReapStuckJobs();
            SweepLongLivedStores();

            // Bound the refresh gate as well: entries whose window has passed are useless, and the
            // gate must not grow with the size of the library.
            var prunedRefreshes = _refreshGate.Prune();
            if (prunedRefreshes > 0)
            {
                _logger.LogDebug(
                    "Library refresh gate: dropped {Pruned} expired entries, {Suppressed} refreshes skipped so far",
                    prunedRefreshes,
                    _refreshGate.SuppressedCount);
            }

            var toRemove = JobsToEvict(
                _jobs, DateTime.UtcNow, JobRetention, MaxTrackedJobs, id => _historyOnlyJobs.Contains(id));

            foreach (var key in toRemove)
            {
                _jobs.TryRemove(key, out _);
                _jobContexts.TryRemove(key, out _);
            }

            if (toRemove.Count > 0)
            {
                lock (_queueLock)
                {
                    _runOrder.RemoveAll(j => toRemove.Contains(j.Id));
                }

                _logger.LogDebug("Cleaned up {Count} old sync jobs", toRemove.Count);
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Error during job cleanup");
        }
    }

    /// <summary>
    /// The ffsubsync flags that decide whether subtitle timings may be rescaled.
    /// </summary>
    /// <remarks>
    /// ffsubsync 0.5.1 corrects a framerate mismatch by default and infers the ratio from the ratio
    /// between the reference duration and the subtitle's own span, so a subtitle whose last cue sits a
    /// few percent outside the video is read as a framerate mismatch and the whole file is time-scaled
    /// to fit. Measured against the bundled engine with a 4.17% longer span: the default, and either
    /// opt-out flag on its own, all produced a 0.960x scale with a -51.9 s shift and -104 s of drift;
    /// only both flags together left the timings alone (ratio 1.0000x, offset only). Correction is
    /// therefore opt-in, and when it is on, the measured result still has to be a real framerate pair.
    /// </remarks>
    /// <param name="fixFramerate">Whether the user asked for framerate correction.</param>
    /// <param name="goldenSection">Whether the user asked for golden-section ratio search.</param>
    /// <returns>The flags to pass, possibly none.</returns>
    internal static IEnumerable<string> FramerateArgs(bool fixFramerate, bool goldenSection)
    {
        if (fixFramerate)
        {
            if (goldenSection)
            {
                yield return "--gss";
            }

            yield break;
        }

        yield return "--no-fix-framerate";
        yield return "--skip-infer-framerate-ratio";
    }

    private List<string> BuildFfSubSyncArgs(
        Configuration.PluginConfiguration config,
        string videoPath,
        string subtitleInput,
        string subtitleOutput,
        string? logDir = null,
        bool serializeSpeech = false,
        string? referenceStream = null,
        string? vadOverride = null)
    {
        // Validate config values to prevent argument injection
        var vadMethod = !string.IsNullOrWhiteSpace(vadOverride) && AllowedVadMethods.Contains(vadOverride)
            ? vadOverride!
            : AllowedVadMethods.Contains(config.VadMethod)
                ? config.VadMethod
                : "subs_then_webrtc";
        // The values the engine is given are the validated ones, so a hand-edited config.xml cannot put a
        // negative or absurd ceiling into argv (F10).
        var outputEncoding = Configuration.SettingsValidation.OutputEncodingOf(config);
        var maxOffsetSeconds = Configuration.SettingsValidation.MaxOffsetSecondsOf(config);
        var maxSubtitleSeconds = Configuration.SettingsValidation.MaxSubtitleSecondsOf(config);

        // ArgumentList passes argv directly — no string-quoting layer, so paths
        // with spaces/unicode can never split into extra arguments.
        var args = new List<string>
        {
            videoPath,
            "-i", subtitleInput,
            "-o", subtitleOutput,
            "--max-offset-seconds", maxOffsetSeconds.ToString(CultureInfo.InvariantCulture),
            "--max-subtitle-seconds", maxSubtitleSeconds.ToString(CultureInfo.InvariantCulture),
            "--vad", vadMethod,
            "--output-encoding", outputEncoding,
            "--ffmpeg-path", ResolveFfmpegPath()
        };

        foreach (var flag in FramerateArgs(config.FixFramerate, config.UseGoldenSectionSearch))
        {
            args.Add(flag);
        }

        // When the alignment reference is itself a subtitle, the engine's span-based ratio inference has no frame
        // rate to read: it compares the reference's duration with the subtitle's span and rescales from that.
        // Measured on a 50-minute fixture whose subtitle was one PAL step off the reference, enabling framerate
        // correction this way turned a 17.4 s shift into a 55.8 s one, and the reference ceiling refused the file.
        // The plugin owns the rescale decision on this path - it can see both spans and only accepts a real
        // framerate pair (RescaleOntoReferenceSpan) - so the inference stays out of it either way.
        var referenceIsSubtitle = referenceStream is null
            && videoPath.EndsWith(".srt", StringComparison.OrdinalIgnoreCase);
        if (referenceIsSubtitle && !args.Contains("--skip-infer-framerate-ratio"))
        {
            args.Add("--skip-infer-framerate-ratio");
        }

        if (!string.IsNullOrWhiteSpace(referenceStream))
        {
            args.Add("--reference-stream");
            args.Add(referenceStream);
        }

        args.AddRange(PiecewiseArgs(config));

        if (serializeSpeech)
        {
            // Writes the speech signal next to the reference path we passed in (a
            // symlink inside our cache dir), so later subtitles of the same file can
            // reuse it instead of analysing the audio again.
            args.Add("--serialize-speech");
        }

        if (!string.IsNullOrWhiteSpace(logDir))
        {
            args.Add("--log-dir-path");
            args.Add(logDir);
        }

        return args;
    }

    /// <summary>
    /// The engine arguments that allow a piecewise alignment, if the setting asks for one.
    /// </summary>
    /// <remarks>
    /// The alass idea, reached through this engine's own <c>--split-penalty</c>: the offset may change across
    /// the timeline, charged this many seconds of overlap per split. 0 - the default, and every release so
    /// far - leaves the engine's single global offset in place.
    /// </remarks>
    /// <param name="config">The stored configuration.</param>
    /// <returns>The arguments to add, empty when a single global offset is wanted.</returns>
    public static List<string> PiecewiseArgs(PluginConfiguration config)
    {
        var penalty = Configuration.SettingsValidation.SplitPenaltyOf(config);
        return penalty > 0
            ? new List<string> { "--split-penalty", penalty.ToString("0.###", CultureInfo.InvariantCulture) }
            : new List<string>();
    }

    /// <summary>
    /// Runs a process and calls back with each stderr line in real-time.
    /// Used for ffsubsync to parse tqdm progress and phase messages.
    /// </summary>
    /// <summary>
    /// Tests a stretch against the film's audio and says what to write.
    /// </summary>
    /// <remarks>
    /// Called only when a stretch happened. If it holds, the engine's own output - the stretched subtitle with that
    /// small residual applied - is the result, or the stretched copy when the engine suppressed a write it judged
    /// too small to save. If it does not hold, the subtitle the user has is aligned against the audio with offsets
    /// only. The caller is told which input that result came from, because the guards downstream compare the result
    /// with what the engine was given.
    /// </remarks>
    /// <param name="job">The job.</param>
    /// <param name="config">The plugin configuration.</param>
    /// <param name="ffsubsyncExe">The engine.</param>
    /// <param name="videoPath">The media file, which supplies the audio.</param>
    /// <param name="stretchedInput">The rescaled subtitle the alignment used.</param>
    /// <param name="originalInput">The subtitle the user has.</param>
    /// <param name="tempDir">The job's temporary directory.</param>
    /// <param name="videoSeconds">The file's duration.</param>
    /// <param name="cancellationToken">Cancellation.</param>
    /// <returns>The path to use as the result, whether the stretch was dropped, and the input that result came from.</returns>
    private async Task<(string? Path, bool Dropped, string Input)> VerifyStretchAgainstAudioAsync(
        SyncJob job,
        Configuration.PluginConfiguration config,
        string ffsubsyncExe,
        string videoPath,
        string stretchedInput,
        string originalInput,
        string tempDir,
        double videoSeconds,
        CancellationToken cancellationToken)
    {
        var verifyOutput = Path.Combine(tempDir, "audio-check.srt");
        SafeDelete(verifyOutput);
        var args = new List<string>
        {
            videoPath,
            "-i", stretchedInput,
            "-o", verifyOutput,
            "--max-offset-seconds",
            Configuration.SettingsValidation.MaxOffsetSecondsOf(config).ToString(CultureInfo.InvariantCulture),
            "--max-subtitle-seconds",
            Configuration.SettingsValidation.MaxSubtitleSecondsOf(config).ToString(CultureInfo.InvariantCulture),
            // The reference of this run is the video, so this is an audio run: it is given the audio VAD (S43).
            "--vad", AudioReferenceVad,
            "--output-encoding", Configuration.SettingsValidation.OutputEncodingOf(config),
            "--ffmpeg-path", ResolveFfmpegPath(),
            "--no-fix-framerate",
            "--skip-infer-framerate-ratio",
            "--log-dir-path", tempDir
        };

        _logger.LogInformation("Sync job {JobId}: testing the stretch against the film's audio", job.Id);
        PluginLog.Info(
            $"[{job.Id}] framerate: the subtitle was stretched, so the stretch is tested against the film's own "
            + "audio (offsets only) \u2014 a differently cut subtitle looks the same as a framerate mismatch in the spans");

        var exitCode = await _processes.RunProcessWithStderrCallbackAsync(
            ffsubsyncExe, args, tempDir, null, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            _logger.LogWarning(
                "Sync job {JobId}: the audio check could not run (exit {Code}) - keeping the stretched result",
                job.Id,
                exitCode);
            PluginLog.Info($"[{job.Id}] framerate: the audio check could not run (exit={exitCode}) - keeping the stretched result");
            return (stretchedInput, false, stretchedInput);
        }

        var measured = File.Exists(verifyOutput) ? AlignmentMetrics.MeasureSyncChange(stretchedInput, verifyOutput) : null;
        var ratio = measured?.Ratio ?? 1.0;
        var shiftMs = measured?.ShiftMs ?? 0;
        if (AlignmentMetrics.AlignmentHoldsAgainstAudio(ratio, shiftMs, videoSeconds))
        {
            _logger.LogInformation(
                "Sync job {JobId}: the stretch holds - the audio asked for {Shift} ms more and no rescale",
                job.Id,
                shiftMs);
            PluginLog.Info(
                $"[{job.Id}] framerate: the audio confirms the stretch (a further {shiftMs} ms, no rescale)");
            return File.Exists(verifyOutput) ? (verifyOutput, false, stretchedInput) : (stretchedInput, false, stretchedInput);
        }

        // The stretch does not hold: align the subtitle the user has against the audio, offsets only.
        var fallbackOutput = Path.Combine(tempDir, "audio-fallback.srt");
        SafeDelete(fallbackOutput);
        var fallbackArgs = new List<string>(args);
        fallbackArgs[fallbackArgs.IndexOf("-i") + 1] = originalInput;
        fallbackArgs[fallbackArgs.IndexOf("-o") + 1] = fallbackOutput;

        _logger.LogWarning(
            "Sync job {JobId}: the stretch does not hold against the audio ({Ratio:0.0000}x, {Shift} ms) - aligning with offsets only",
            job.Id,
            ratio,
            shiftMs);
        PluginLog.Info(
            $"[{job.Id}] framerate: the stretch does NOT hold against the audio ({ratio:0.0000}x, {shiftMs} ms) "
            + "\u2014 a different cut looks the same in the spans, so the subtitle is aligned with offsets only");

        var fallbackExit = await _processes.RunProcessWithStderrCallbackAsync(
            ffsubsyncExe, fallbackArgs, tempDir, null, cancellationToken).ConfigureAwait(false);
        if (fallbackExit == 0 && File.Exists(fallbackOutput))
        {
            return (fallbackOutput, true, originalInput);
        }

        return (null, true, originalInput);
    }

    // One definition of the plugin log's completion line (S21). It used to be written inline on the
    // success path only, so a job that finished having written nothing logged the fact to Jellyfin's log
    // and nowhere else: a batch could never be reconciled from the plugin log alone, and the tail watcher
    // never saw those jobs at all. Every completed job now produces this line, whatever it wrote.
    private static void LogPluginCompletion(SyncJob job, long? outputSize)
        => PluginLog.Info(
            $"job {job.Id} completed: mode={job.Mode} output={job.OutputPath ?? "(none)"} "
            + $"bytes={outputSize?.ToString() ?? "unknown"} change={job.Outcome ?? "unknown"} "
            + $"extraction={job.ExtractionNote ?? "n/a"}");

    // watch (optional): when set, a heartbeat line is written to the plugin's own log every few minutes
    // for as long as the process runs (S27). Visibility only - no deadline and no kill is added by it.
    //
    // jobId (optional): the job this process belongs to, so the stall watchdog can tell "waiting for a slow
    // read" from "wedged" (B6). Every process the plugin runs for a job registers here: a live child is work
    // in progress even when it is quiet, and its own output is what dates its silence.
}
