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

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, DateTime> _ceilingLogged =
        new(StringComparer.Ordinal);

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
    /// Upper bound for <see cref="Configuration.PluginConfiguration.ParallelWorkers"/>.
    ///
    /// This is not a performance opinion: it exists so a mistyped value (10000) cannot spawn
    /// thousands of processes. Everything from 1 upwards is honoured as written — 1, 3, 32 all
    /// mean exactly what they say, and the setting is the only thing that decides the width.
    /// </summary>
    public const int MaxParallelWorkers = 64;

    private static int _lastLoggedWorkerLimit = -1;

    /// <summary>Default worker count for parallel mode.</summary>
    public const int DefaultParallelWorkers = 4;

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
    /// Regex to extract tqdm percentage from ffsubsync stderr.
    /// Matches patterns like " 42%|..." at the start of a line.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex TqdmPercentRegex =
        new(@"^\s*(\d+)%\|", System.Text.RegularExpressions.RegexOptions.Compiled);

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
