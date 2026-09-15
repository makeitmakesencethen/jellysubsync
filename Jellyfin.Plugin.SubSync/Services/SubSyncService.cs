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

    /// <summary>Gets or sets whether a system python3 was found.</summary>
    public bool PythonAvailable { get; set; }

    /// <summary>Gets or sets the python3 version string, if found.</summary>
    public string? PythonVersion { get; set; }

    /// <summary>Gets or sets the ffsubsync version string, if installed.</summary>
    public string? FfSubSyncVersion { get; set; }

    /// <summary>Gets or sets a summary of the cached speech analysis used by fast mode.</summary>
    public string? SpeechCacheSummary { get; set; }

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
public class SubSyncService : IDisposable
{
    private readonly ILogger<SubSyncService> _logger;
    /// <summary>
    /// Whether an account may act on an item: the same check the item-scoped API endpoints make before
    /// they read anything or queue anything.
    /// </summary>
    /// <remarks>
    /// Fails closed at every step. An account that cannot be resolved, an item that cannot be resolved, a
    /// user with no libraries and an item with no ancestors all deny.
    /// </remarks>
    /// <param name="userId">The calling account.</param>
    /// <param name="itemId">The item the request names.</param>
    /// <returns>True when the account may act on the item.</returns>
    public bool CanUserSeeItem(Guid userId, Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is null)
        {
            return false;
        }

        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            return false;
        }

        // The account's libraries. Read through the preference API rather than a named type: Jellyfin
        // moved its entity namespace between versions, and this plugin must not care which one it built
        // against. Anything unexpected here denies, because the alternative is allowing.
        IReadOnlyCollection<string> folders;
        try
        {
            var enabled = user.GetPreference(PreferenceKind.EnabledFolders);
            folders = enabled is null
                ? Array.Empty<string>()
                : enabled.Where(f => !string.IsNullOrWhiteSpace(f)).ToArray();
        }
        catch (Exception)
        {
            folders = Array.Empty<string>();
        }

        var allFolders = user.HasPermission(PermissionKind.EnableAllFolders);
        var ancestors = AncestorIds(item);
        var allowed = ItemAccess.Allows(allFolders, folders, ancestors);
        if (!allowed)
        {
            // One line, both sides of the comparison. This is what makes a refusal diagnosable in the field:
            // if a legitimate account is ever refused, the log says which folders the item was found in and
            // which libraries the account holds, so the check can be corrected rather than guessed at.
            PluginLog.Info(
                $"item {itemId} not visible to account {userId}: it sits under [{string.Join(", ", ancestors)}], "
                + $"and that account's libraries are [{string.Join(", ", folders)}] (all-folders={allFolders})");
        }

        return allowed;
    }

    /// <summary>
    /// Gets the first item in a request that the account may not act on, or null when it may act on all
    /// of them. The bulk endpoints refuse the whole request when this returns anything.
    /// </summary>
    /// <param name="userId">The calling account.</param>
    /// <param name="itemIds">Every item the request names.</param>
    /// <returns>The first denied item id, or null.</returns>
    public Guid? FirstItemNotVisibleTo(Guid userId, IEnumerable<Guid> itemIds)
    {
        var user = _userManager.GetUserById(userId);
        if (user is null)
        {
            // The account itself cannot be resolved: refuse the request rather than each item, because
            // there is nothing to check any of them against.
            return itemIds.FirstOrDefault();
        }

        foreach (var itemId in itemIds)
        {
            if (!CanUserSeeItem(userId, itemId))
            {
                return itemId;
            }
        }

        return null;
    }

    /// <summary>
    /// Gets the ids of every folder an item sits under, itself excluded.
    /// </summary>
    /// <param name="item">The item.</param>
    /// <returns>Folder ids, nearest first.</returns>
    private static IReadOnlyCollection<string> AncestorIds(BaseItem item)
    {
        var ids = new List<string>();
        for (var parent = item.GetParent(); parent is not null; parent = parent.GetParent())
        {
            ids.Add(parent.Id.ToString("N"));
        }

        return ids;
    }

    private readonly ILibraryManager _libraryManager;
    private readonly IUserManager _userManager;
    private readonly ILibraryMonitor _libraryMonitor;

    // Lets the item refresh run once per item instead of once per subtitle track; the folder report
    // that makes Jellyfin discover the file is never suppressed.
    private readonly LibraryRefreshGate _refreshGate = new();
    private readonly ConcurrentDictionary<string, SyncJob> _jobs = new();

    // Track whether an installation is currently in progress
    private int _installing;

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
    /// Every child process currently running (ffsubsync, ffmpeg), so Kill can terminate the
    /// whole tree instead of only cancelling a token and hoping the process notices.
    /// </summary>
    private readonly ConcurrentDictionary<int, Process> _liveProcesses = new();

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

        // Evict completed/failed jobs older than 1 hour, check every 30 minutes
        _cleanupTimer = new Timer(_ => CleanupOldJobs(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));

        RestoreBatchHistory();
    }

    /// <summary>
    /// Gets the path to the ffsubsync binary inside the managed virtualenv.
    /// </summary>
    public string ManagedFfSubSyncPath => Path.Join(Plugin.Instance?.VenvPath, "bin", "ffsubsync");

    /// <summary>
    /// Gets the path to the pip binary inside the managed virtualenv.
    /// </summary>
    public string ManagedPipPath => Path.Join(Plugin.Instance?.VenvPath, "bin", "pip");

    /// <summary>
    /// Gets the path to the python3 binary inside the managed virtualenv.
    /// </summary>
    public string ManagedPythonPath => Path.Join(Plugin.Instance?.VenvPath, "bin", "python3");

    /// <summary>
    /// Resolves the actual ffsubsync executable to use.
    /// Priority: bundled platform binary → user-configured path → managed venv → system PATH.
    /// The bundled binary (PyInstaller, ships its own Python) is what makes the
    /// plugin work with zero setup on common Linux servers.
    /// </summary>
    /// <returns>The ffsubsync executable path.</returns>
    public string ResolveFfSubSyncPath()
    {
        var config = Services.SettingsSource.Current();

        // Explicit admin override wins over everything: if the user configured a
        // custom path (non-empty, non-default), honor it even when a bundled
        // binary exists — the setting must stay usable as an override.
        if (config is not null
            && !string.IsNullOrWhiteSpace(config.FfSubSyncPath)
            && config.FfSubSyncPath != "ffsubsync")
        {
            return config.FfSubSyncPath;
        }

        // Zero-setup default: plugin-shipped binary for this platform.
        var bundled = BundledFfSubSyncPath;
        if (bundled is not null)
        {
            return bundled;
        }

        // Check managed venv
        if (File.Exists(ManagedFfSubSyncPath))
        {
            return ManagedFfSubSyncPath;
        }

        // Fall back to system PATH
        return "ffsubsync";
    }

    /// <summary>
    /// Gets the bundled ffsubsync executable for the current platform, if the
    /// plugin shipped one (layout: plugin folder, ffsubsync subfolder, rid folder).
    /// </summary>
    public string? BundledFfSubSyncPath
    {
        get
        {
            var rid = RuntimeInformation.OSArchitecture switch
            {
                Architecture.X64 when OperatingSystem.IsLinux() => "linux-x64",
                Architecture.Arm64 when OperatingSystem.IsLinux() => "linux-arm64",
                Architecture.X64 when OperatingSystem.IsWindows() => "win-x64",
                _ => null
            };

            if (rid is null)
            {
                return null;
            }

            var pluginDir = Path.GetDirectoryName(typeof(Plugin).Assembly.Location);
            if (string.IsNullOrEmpty(pluginDir))
            {
                return null;
            }

            var exeName = OperatingSystem.IsWindows() ? "ffsubsync.exe" : "ffsubsync";
            var candidate = Path.Combine(pluginDir, "ffsubsync", rid, exeName);
            if (!File.Exists(candidate))
            {
                return null;
            }

            // Jellyfin extracts plugin zips with System.IO.Compression, which does
            // not preserve Unix executable permissions — restore the bit so the
            // bundled PyInstaller launcher can actually be spawned.
            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(
                        candidate,
                        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                        UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                        UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
                }
                catch
                {
                    // Best effort — the sync will surface a clear error if it fails.
                }
            }

            return candidate;
        }
    }

    /// <summary>
    /// Gets the version of the bundled ffsubsync binary, if present.
    /// </summary>
    public string? BundledFfSubSyncVersion
    {
        get
        {
            var path = BundledFfSubSyncPath;
            if (path is null)
            {
                return null;
            }

            try
            {
                var (exitCode, output) = RunProcessCaptureAsync(path, "--version", null).GetAwaiter().GetResult();
                return exitCode == 0 ? output.Trim() : null;
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Resolves the ffmpeg path: config → Jellyfin's own ffmpeg → system PATH.
    /// Jellyfin always ships an ffmpeg; auto-detecting it means Docker users
    /// never need to configure anything.
    /// </summary>
    private string ResolveFfmpegPath()
    {
        var config = Services.SettingsSource.Current();
        // A configured path is only usable when it is there: a typo used to be handed to the engine as
        // --ffmpeg-path, which then had nothing to read the file with (D3/F10).
        if (config is not null && !string.IsNullOrWhiteSpace(config.FfmpegPath)
            && Configuration.SettingsValidation.BinaryPathIsUsable(config.FfmpegPath))
        {
            return config.FfmpegPath;
        }

        // Official Docker images expose it via env; linuxserver images ship the
        // same standard path.
        var jellyfinFfmpeg = Environment.GetEnvironmentVariable("JELLYFIN_FFMPEG");
        if (!string.IsNullOrWhiteSpace(jellyfinFfmpeg) && File.Exists(jellyfinFfmpeg))
        {
            return jellyfinFfmpeg;
        }

        if (OperatingSystem.IsLinux() && File.Exists("/usr/lib/jellyfin-ffmpeg/ffmpeg"))
        {
            return "/usr/lib/jellyfin-ffmpeg/ffmpeg";
        }

        return "ffmpeg";
    }

    /// <summary>
    /// Checks the installation status of ffsubsync.
    /// </summary>
    /// <returns>Detailed installation status.</returns>
    public async Task<FfSubSyncInstallationStatus> GetInstallationStatusAsync()
    {
        var status = new FfSubSyncInstallationStatus
        {
            VenvPath = Plugin.Instance?.VenvPath,
            ManagedBinaryPath = ManagedFfSubSyncPath,
            IsInstalled = File.Exists(ManagedFfSubSyncPath),

            // Where the plugin was loaded from and which settings file it reads: two loaded copies
            // would each keep their own configuration, and the settings page would then write to
            // one while the other ran the queue.
            PluginIdentity = Plugin.Instance is { } plugin
                ? plugin.AssemblyLocation + "  ·  " + plugin.SettingsFilePath
                : "plugin instance unavailable",

            // The value in force next to the configured one, so a disagreement is visible without
            // reading any code.
            WorkerSummary = $"{EffectiveWorkerLimit} in use (setting {ConfiguredWorkerLimit})",

            // Which build is answering, shown beside the ffsubsync badge instead of inside the
            // progress line.
            PluginVersion = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? string.Empty,

            // Where the plugin's own log is, so it can be opened from the interface.
            LogFile = PluginLog.Describe()
        };

        // Check system python3
        try
        {
            var (exitCode, stdout) = await RunProcessCaptureAsync("python3", "--version", null).ConfigureAwait(false);
            if (exitCode == 0 && !string.IsNullOrWhiteSpace(stdout))
            {
                status.PythonAvailable = true;
                status.PythonVersion = stdout.Trim();
            }
        }
        catch
        {
            // python3 not found
        }

        // Check managed ffsubsync version
        if (status.IsInstalled)
        {
            try
            {
                var (exitCode, stdout) = await RunProcessCaptureAsync(ManagedFfSubSyncPath, "--version", null).ConfigureAwait(false);
                if (exitCode == 0)
                {
                    status.FfSubSyncVersion = stdout.Trim();
                }
            }
            catch
            {
                // ignore
            }
        }

        // Bundled (plugin-shipped) binary takes precedence over everything else.
        var bundledPath = BundledFfSubSyncPath;
        if (bundledPath is not null)
        {
            status.BundledFfSubSyncVersion = BundledFfSubSyncVersion;
            status.BundledRid = Path.GetFileName(Path.GetDirectoryName(bundledPath));
        }

        status.ResolvedBinaryPath = ResolveFfSubSyncPath();
        status.SpeechCacheSummary = SpeechCache.Describe()
            + " \u00b7 references: " + ReferenceStore.Describe()
            + " \u00b7 extracted subtitles: " + SubtitleCache.Describe();
        return status;
    }

    /// <summary>
    /// Installs ffsubsync into the managed virtualenv.
    /// Creates the venv if it doesn't exist, then pip-installs ffsubsync.
    /// </summary>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">Thrown when installation fails.</exception>
    public async Task InstallFfSubSyncAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _installing, 1, 0) == 1)
        {
            throw new InvalidOperationException("Installation is already in progress.");
        }

        try
        {
            // Our improvement over upstream: the official Jellyfin Docker image
            // ships no python3 at all (and Debian splits python3-venv from
            // python3), which made upstream's install fail with a bare HTTP 400.
            // Provision python3 + venv support automatically when missing, so a
            // stock container works out of the box.
            await EnsurePythonAvailableAsync().ConfigureAwait(false);

            var venvPath = Plugin.Instance?.VenvPath
                ?? throw new InvalidOperationException("Plugin not initialized.");

            // venvPath is plugin-derived ({DataPath}/subsync/venv) — never raw user
            // input — so no traversal containment check is needed here. (A former
            // GetFullPath(venvPath).Contains("..") check was dead code: GetFullPath
            // already resolves ".." segments, so it could never trigger.)

            // Step 1: Create virtualenv
            if (!Directory.Exists(venvPath) || !File.Exists(ManagedPythonPath))
            {
                _logger.LogInformation("Creating Python virtualenv at {Path}", venvPath);
                var (exitCode, output) = await RunProcessCaptureAsync("python3", $"-m venv {EscapeArg(venvPath)}", null).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to create virtualenv: {output}");
                }
            }

            // Step 2: Upgrade pip
            _logger.LogInformation("Upgrading pip in virtualenv");
            var pipExit = await RunProcessAsync(ManagedPythonPath, "-m pip install --upgrade pip", null, cancellationToken).ConfigureAwait(false);
            if (pipExit != 0)
            {
                _logger.LogWarning("pip upgrade failed with exit code {Code}, continuing anyway", pipExit);
            }

            // Step 3: Install ffsubsync + pin setuptools<81 (webrtcvad needs pkg_resources)
            _logger.LogInformation("Installing ffsubsync into virtualenv");
            var installExit = await RunProcessAsync(ManagedPipPath, "install ffsubsync \"setuptools<81\"", null, cancellationToken).ConfigureAwait(false);
            if (installExit != 0)
            {
                throw new InvalidOperationException($"pip install ffsubsync failed with exit code {installExit}.");
            }

            if (!File.Exists(ManagedFfSubSyncPath))
            {
                throw new InvalidOperationException("ffsubsync was installed but the binary was not found at the expected path.");
            }

            _logger.LogInformation("ffsubsync installed successfully at {Path}", ManagedFfSubSyncPath);
        }
        finally
        {
            Interlocked.Exchange(ref _installing, 0);
        }
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
    private static bool IsOwnSidecar(MediaBrowser.Model.Entities.MediaStream stream) =>
        stream.IsExternal && SrtWriter.IsSyncedSidecarName(stream.Path);

    /// <summary>
    /// Lists the subtitle tracks of one item, as the UI offers them.
    /// </summary>
    /// <param name="itemId">Media item.</param>
    /// <returns>The selectable subtitle tracks, or null when the item is not a video.</returns>
    public List<SubtitleInfo>? ListSubtitles(Guid itemId)
    {
        var item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            return null;
        }

        var mediaSources = video.GetMediaSources(true);
        if (mediaSources.Count == 0)
        {
            return new List<SubtitleInfo>();
        }

        var source = mediaSources[0];
        var languageFilter = Services.SettingsSource.Current()?.SyncLanguages ?? Array.Empty<string>();

        // Image-based tracks (PGS, VobSub, DVB, XSUB) can never be aligned — they are
        // left out entirely so they cannot be picked and fail. Tracks outside the
        // configured language filter are hidden too, so the UI only ever offers work
        // that can actually succeed.
        return source.MediaStreams
            .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle)
            .Where(s => !LanguageSupport.IsImageBased(s.Codec))
            .Where(s => !IsOwnSidecar(s))
            .Where(s => LanguageSupport.MatchesFilter(s.Language, languageFilter))
            .Select(s =>
            {
                return new SubtitleInfo
                {
                    Index = s.Index,
                    Title = s.DisplayTitle ?? s.Language ?? $"Track {s.Index}",
                    Language = s.Language ?? "und",
                    IsExternal = s.IsExternal,
                    IsForced = s.IsForced,
                    ExternalPath = s.Path,
                    HasSyncedVersion = HasCompletedSync(itemId, s.Index)
                };
            })
            .ToList();
    }

    /// <summary>
    /// Lists subtitle streams for many items in one call, optionally expanding series and
    /// seasons into their episodes.
    ///
    /// The library browser used to ask for one item (and then one episode) at a time, so a
    /// library-wide selection meant hundreds of sequential round-trips before anything could
    /// be queued. Reading the media streams is in-memory work, so doing it server-side for a
    /// whole chunk of items is roughly free.
    /// </summary>
    /// <param name="itemIds">Items to look up.</param>
    /// <param name="expandSeries">Whether to expand series/seasons into episodes.</param>
    /// <param name="maxEpisodesPerSeries">Safety cap on expansion per requested series.</param>
    /// <returns>One entry per movie/episode, with the episodes of a series carrying its id.</returns>
    public List<BulkSubtitleItem> ListSubtitlesBulk(
        IReadOnlyList<Guid> itemIds,
        bool expandSeries,
        int maxEpisodesPerSeries = 2000)
    {
        var result = new List<BulkSubtitleItem>();
        if (itemIds is null || itemIds.Count == 0)
        {
            return result;
        }

        foreach (var itemId in itemIds)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item is null)
            {
                continue;
            }

            if (item is Video video)
            {
                result.Add(new BulkSubtitleItem
                {
                    Id = video.Id,
                    Name = video.Name,
                    Tracks = ListSubtitles(video.Id) ?? new List<SubtitleInfo>()
                });
                continue;
            }

            if (!expandSeries)
            {
                continue;
            }

            if (item is not Folder folder)
            {
                continue;
            }

            IEnumerable<BaseItem> children;
            try
            {
                children = folder.GetRecursiveChildren();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not expand {ItemId} into episodes", itemId);
                continue;
            }

            var count = 0;
            foreach (var child in children.OfType<Video>())
            {
                if (count++ >= maxEpisodesPerSeries)
                {
                    break;
                }

                result.Add(new BulkSubtitleItem
                {
                    Id = child.Id,
                    SeriesId = itemId,
                    Name = child.Name,
                    Tracks = ListSubtitles(child.Id) ?? new List<SubtitleInfo>()
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Starts a sync job for the given item and subtitle stream index.
    /// Automatically ensures ffsubsync is available before running.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="subtitleIndex">The subtitle stream index within the first media source.</param>
    /// <param name="mode">Multi-subtitle mode (normal | parallel | fast).</param>
    /// <returns>The created sync job.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the video file is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the subtitle stream is not found or ffsubsync is unavailable.</exception>
    public SyncJob StartSync(Guid itemId, int subtitleIndex, string? mode = null)
    {
        return EnqueueSync(itemId, subtitleIndex, label: null, batchId: null, batchLabel: null, batchIndex: -1, mode: mode);
    }

    /// <summary>
    /// Creates and queues a sync job. Validation (item is a video, subtitle
    /// stream exists) happens NOW so bad requests fail immediately; execution
    /// happens later via the single background pump.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="subtitleIndex">The subtitle stream index within the first media source.</param>
    /// <param name="label">Optional human label (subtitle/track title).</param>
    /// <param name="batchId">Batch this job belongs to, if any.</param>
    /// <param name="batchLabel">Batch scope label, if any.</param>
    /// <param name="batchIndex">0-based position inside the batch.</param>
    /// <param name="mode">Multi-subtitle mode (normal | parallel | fast).</param>
    /// <returns>The queued sync job.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the video file is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the subtitle stream is not found.</exception>
    public SyncJob EnqueueSync(Guid itemId, int subtitleIndex, string? label, string? batchId, string? batchLabel, int batchIndex, string? mode = null)
    {
        return EnqueueSyncTimed(itemId, subtitleIndex, label, batchId, batchLabel, batchIndex, mode).Job;
    }

    /// <summary>
    /// Queues one task and reports where the time went.
    /// </summary>
    /// <remarks>
    /// Queueing has to be fast: the scheduler can only start what is already in the queue, and a
    /// batch that trickles in one task every few seconds therefore looks exactly like a plugin that
    /// refuses to run more than one job at a time. The parts are timed so the slow one can be named
    /// instead of guessed at.
    /// </remarks>
    /// <param name="itemId">Media item.</param>
    /// <param name="subtitleIndex">Subtitle stream index.</param>
    /// <param name="label">Display label.</param>
    /// <param name="batchId">Batch this task belongs to.</param>
    /// <param name="batchLabel">Batch label.</param>
    /// <param name="batchIndex">Position within the batch.</param>
    /// <param name="mode">Requested mode.</param>
    /// <returns>
    /// The queued job, the cost of each part, and whether the queue already held a job for this item and track
    /// (in which case that job is the one returned - D9).
    /// </returns>
    internal (SyncJob Job, string Timing, bool Duplicate) EnqueueSyncTimed(
        Guid itemId,
        int subtitleIndex,
        string? label,
        string? batchId,
        string? batchLabel,
        int batchIndex,
        string? mode = null)
    {
        var total = System.Diagnostics.Stopwatch.StartNew();
        var phase = System.Diagnostics.Stopwatch.StartNew();
        long itemMs = 0, sourcesMs = 0, settingsMs = 0, logMs = 0;
        long stateMs = 0;        // the two dictionary writes: what the enqueue records about the job
        long logWriteMs = 0;    // one line to the plugin log, which serialises every writer
        long lockMs = 0;        // the queue lock, also held by the pump while it plans
        long wakeMs = 0;        // waking the pump: starts extraction lanes, takes the lock twice more
        if (subtitleIndex < 0)
        {
            throw new ArgumentException("Subtitle index must be non-negative.");
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            throw new InvalidOperationException($"Item {itemId} is not a video.");
        }

        // No filesystem access in the enqueue path. This check used to stat the media file per task,
        // and with a job already reading that share each stat took seconds: a 50-task batch was
        // enqueued over a minute, so the scheduler only ever saw a handful of queued jobs and could
        // not fill its workers ("starting 1 of 8"). A missing file is caught when the job runs, with
        // the same clear message.
        // Validation therefore works from Jellyfin's cached metadata only.
        itemMs = phase.ElapsedMilliseconds;
        phase.Restart();
        var mediaSources = video.GetMediaSources(true);
        if (mediaSources.Count == 0)
        {
            throw new InvalidOperationException("No media sources found for the video.");
        }

        var source = mediaSources[0];
        var subtitleStream = source.MediaStreams
            .FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && s.Index == subtitleIndex);

        if (subtitleStream is null)
        {
            throw new InvalidOperationException($"Subtitle stream index {subtitleIndex} not found.");
        }

        // Embedded extraction never trusts Jellyfin's stream numbering: the real container
        // stream index is resolved at run time by probing the file with ffmpeg (see
        // ResolveContainerSubtitleIndexAsync). What the rest of the service means by "the
        // subtitle's ordinal" is its position among the file's embedded subtitle tracks - the
        // numbering the extraction lane, the subtitle cache and ffmpeg's own 0:s:N speak. That is
        // not Jellyfin's MediaStream.Index, which counts every stream in the file, video and audio
        // included, so the two differ by however many streams sit in front of the subtitles.
        // Handing the index to code that wanted an ordinal refused five jobs on a real run
        // ("subtitle ordinal 11 out of range (11 tracks)"), and on files where the index happened
        // to land inside the range it made the lane read a neighbouring track instead.
        var subtitleOrdinal = EmbeddedSubtitleOrdinal(source.MediaStreams, subtitleStream);

        var config = Services.SettingsSource.Current() ?? new Configuration.PluginConfiguration();
        sourcesMs = phase.ElapsedMilliseconds;
        phase.Restart();

        _logger.LogInformation(
            "Queued sync: item {ItemId} subtitle stream {SubtitleIndex} — output mode: {Mode}",
            itemId, subtitleIndex, config.SyncModeCopy ? "copy (.SYNCED.srt)" : "replace original in place");

        // One job per item and track (D9): asking twice used to queue two jobs, so the same subtitle was read,
        // synced and written twice while the second waited for the first one's file gate. The queued job is
        // returned instead, and the caller can say "already queued" instead of implying a second run exists.
        lock (_queueLock)
        {
            var alreadyQueued = FindDuplicate(_runOrder, itemId, subtitleIndex);
            if (alreadyQueued is not null)
            {
                total.Stop();
                PluginLog.Info(
                    $"queue duplicate: item={itemId} stream={subtitleIndex} existing={alreadyQueued.Id} "
                    + $"status={alreadyQueued.Status} batch={alreadyQueued.BatchId ?? "(standalone)"}");
                return (alreadyQueued, $"totalMs={total.ElapsedMilliseconds} duplicate=1", true);
            }
        }

        var job = new SyncJob
        {
            ItemId = itemId,
            SubtitleIndex = subtitleIndex,
            BatchId = batchId,
            BatchLabel = batchLabel,
            BatchIndex = batchIndex,
            Label = label,
            Mode = NormalizeMode(mode ?? config.MultiSyncMode)
        };
        settingsMs = phase.ElapsedMilliseconds;
        phase.Restart();

        _jobs[job.Id] = job;
        _jobContexts[job.Id] = (video, subtitleStream, subtitleOrdinal, config);
        stateMs = phase.ElapsedMilliseconds;
        phase.Restart();

        var queuedLine = $"queued: job={job.Id} item={itemId} stream={subtitleIndex} ordinal={subtitleOrdinal} "
            + $"mode={job.Mode} batch={batchId ?? "(standalone)"} language={subtitleStream.Language ?? "und"} "
            + $"external={subtitleStream.IsExternal} forced={subtitleStream.IsForced} "
            + $"codec={subtitleStream.Codec} video={video.Path}";
        PluginLog.Info(queuedLine);
        logWriteMs = phase.ElapsedMilliseconds;
        phase.Restart();

        lock (_queueLock)
        {
            _runOrder.Add(job);
        }

        lockMs = phase.ElapsedMilliseconds;
        phase.Restart();

        WakePump();
        wakeMs = phase.ElapsedMilliseconds;
        logMs = stateMs + logWriteMs + lockMs + wakeMs;
        total.Stop();

        var timing = $"item={itemMs} ms, sources={sourcesMs} ms, settings={settingsMs} ms, "
            + $"log={logMs} ms, total={total.ElapsedMilliseconds} ms";

        // Anything beyond a few milliseconds here is worth naming: a slow enqueue is invisible in
        // every other view and looks exactly like a scheduler that will not parallelise.
        //
        // The breakdown is what S40 needs: the phase the field reported as `log` is four different things -
        // two dictionary writes, one line to the plugin log, the queue lock, and waking the pump (which
        // takes the same lock twice more) - and 8-21 s per queued item has to be attributed to one of them
        // before anything is changed. The plugin log serialises every writer on one gate, the queue lock is
        // held by the pump while it plans, and waking the pump starts extraction lanes: all three are
        // candidates that the aggregate cannot tell apart.
        if (total.ElapsedMilliseconds > EnqueueTraceMs)
        {
            PluginLog.Info(
                $"enqueue slow: {timing} stream={subtitleIndex} breakdown: state={stateMs} ms, "
                + $"logWrite={logWriteMs} ms, queueLock={lockMs} ms, wakePump={wakeMs} ms "
                + $"video={video.Path}");
        }

        return (job, timing, false);
    }

    /// <summary>
    /// Enqueues a batch of tasks as one FIFO unit. Tasks that fail validation
    /// are recorded as failed jobs inside the batch instead of aborting it.
    /// </summary>
    /// <param name="label">Scope label shown in history (e.g. "Series · Season 2").</param>
    /// <param name="tasks">The task list (item, subtitle index, display title).</param>
    /// <param name="mode">Multi-subtitle mode for the whole batch (normal | parallel | fast).</param>
    /// <returns>What the bulk enqueue created, and which requested tasks were already queued (D9).</returns>
    public BatchCreation CreateBatch(string label, IReadOnlyList<(Guid ItemId, int SubtitleIndex, string? Title)> tasks, string? mode = null)
    {
        var batchId = Guid.NewGuid().ToString("N");
        var resolvedMode = NormalizeMode(mode ?? Services.SettingsSource.Current()?.MultiSyncMode);
        var jobs = new List<SyncJob>(tasks.Count);
        var alreadyQueued = new List<SyncJob>();
        var batchWatch = System.Diagnostics.Stopwatch.StartNew();
        var slowestMs = 0L;
        var slowestIndex = -1;
        var slowestDetail = string.Empty;
        var previousEnd = 0L;

        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            var taskWatch = System.Diagnostics.Stopwatch.StartNew();
            // Time spent between the previous task and this one, inside this loop. If a queue fills
            // slowly but every task's own parts are fast, the cost is here, not in the task.
            var gapMs = taskWatch.ElapsedMilliseconds - previousEnd;
            var detail = string.Empty;
            try
            {
                var queued = EnqueueSyncTimed(task.ItemId, task.SubtitleIndex, task.Title, batchId, label, i, resolvedMode);
                detail = queued.Timing;
                if (queued.Duplicate)
                {
                    // Already queued or running: not counted as a task of this batch, because the job belongs to
                    // the batch that queued it and counting it here would show the same work twice (D9).
                    alreadyQueued.Add(queued.Job);
                }
                else
                {
                    jobs.Add(queued.Job);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or FileNotFoundException or ArgumentException)
            {
                _logger.LogWarning("Batch {BatchId} task {Index} failed validation: {Error}", batchId, i, ex.Message);
                var failed = new SyncJob
                {
                    ItemId = task.ItemId,
                    SubtitleIndex = task.SubtitleIndex,
                    BatchId = batchId,
                    BatchLabel = label,
                    BatchIndex = i,
                    Label = task.Title,
                    Mode = resolvedMode,
                    Status = SyncJobStatus.Failed,
                    Error = ex.Message,
                    FinishedAtUtc = DateTime.UtcNow
                };
                _jobs[failed.Id] = failed;
                jobs.Add(failed);
            }

            taskWatch.Stop();
            previousEnd = taskWatch.ElapsedMilliseconds;
            if (taskWatch.ElapsedMilliseconds > slowestMs)
            {
                slowestMs = taskWatch.ElapsedMilliseconds;
                slowestIndex = i;
                slowestDetail = $"gap={gapMs} ms; {detail}";
            }
        }

        batchWatch.Stop();

        // How long it took to get the whole batch into the queue, and which part of the slowest task
        // was slow. This matters more than it looks: while a batch is still being enqueued the
        // scheduler only sees the first few tasks, so a slow enqueue looks exactly like "the plugin
        // refuses to run more than one at a time".
        PluginLog.Info(
            $"batch {batchId} queued: tasks={tasks.Count} mode={resolvedMode} label='{label}' "
            + $"totalMs={batchWatch.ElapsedMilliseconds} slowestTaskMs={slowestMs} (index {slowestIndex}: {slowestDetail})");

        return new BatchCreation { BatchId = batchId, Jobs = jobs, AlreadyQueued = alreadyQueued };
    }

    /// <summary>
    /// Cancels all queued (not yet started) jobs of a batch. A running job is
    /// allowed to finish.
    /// </summary>
    /// <param name="batchId">The batch identifier.</param>
    public void CancelBatch(string batchId)
    {
        var queuedCancelled = 0;
        List<SyncJob> cancelledHere;
        var cancelHold = System.Diagnostics.Stopwatch.StartNew();
        lock (_queueLock)
        {
            // Only the status changes while the lock is held. A log line per cancelled job used to be written
            // here - Jellyfin's file sink and the plugin log both, once per job - so cancelling a large batch
            // held the lock the enqueue path waits on for as long as those writes took (S40).
            cancelledHere = _runOrder
                .Where(j => j.BatchId == batchId && j.Status == SyncJobStatus.Queued)
                .ToList();
            foreach (var job in cancelledHere)
            {
                job.Status = SyncJobStatus.Cancelled;
                job.FinishedAtUtc = DateTime.UtcNow;
            }

            queuedCancelled += cancelledHere.Count;
        }

        cancelHold.Stop();
        LogLockHold("cancel-batch", cancelHold.ElapsedMilliseconds);
        foreach (var job in cancelledHere)
        {
            _logger.LogInformation("Cancelled queued job {JobId} of batch {BatchId}", job.Id, batchId);
            LogPluginCancellation(job);
        }

        // A cancel that leaves the batch running is the complaint it always produces ("I pressed it and
        // it kept going"). The jobs of this batch that are already running are stopped too, and the
        // plugin log records which phases they were in, because a kill that silently misses a job in
        // "Analyzing speech" is indistinguishable from a kill that never arrived.
        var stopped = 0;
        var phases = new List<string>();
        foreach (var job in _jobs.Values.Where(j => j.BatchId == batchId && j.Status == SyncJobStatus.Running))
        {
            phases.Add($"{job.Id[..8]}={job.Phase}");
            if (_jobCancellation.TryGetValue(job.Id, out var cts))
            {
                try
                {
                    cts.Cancel();
                    stopped++;
                    LogPluginCancellation(job);
                }
                catch (ObjectDisposedException)
                {
                    // Finished between the check and the cancel.
                }
            }
        }

        if (queuedCancelled > 0 || stopped > 0)
        {
            PluginLog.Info(
                $"cancel batch {batchId}: {queuedCancelled} queued cancelled, {stopped} running stopped"
                + (phases.Count == 0 ? string.Empty : $" (phases: {string.Join(", ", phases)})"));
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
    /// Formats the periodic progress line. Pure, so a check can pin the wording.
    /// </summary>
    /// <param name="queued">Jobs still queued.</param>
    /// <param name="running">Jobs running now.</param>
    /// <param name="filesLeft">Distinct media files still queued.</param>
    /// <param name="laneFile">The file the extraction lane is on, when it is on one.</param>
    /// <param name="laneElapsed">How long it has been on that file.</param>
    /// <returns>The log line.</returns>
    public static string DescribeProgress(int queued, int running, int filesLeft, string? laneFile, TimeSpan? laneElapsed)
        => $"queue: {queued} queued, {running} running, {filesLeft} file(s) left, "
           + (laneFile is null
               ? "lane idle"
               : $"lane currently on: {laneFile}, {(laneElapsed ?? TimeSpan.Zero).TotalMinutes:0.#} min elapsed on it");

    private static void LogPluginProgress(int queued, int running, int filesLeft, string? laneFile, TimeSpan? laneElapsed)
        => PluginLog.Info(DescribeProgress(queued, running, filesLeft, laneFile, laneElapsed));

    // S25: batches live in memory, and a restart used to lose the History tab entirely. The file is the
    // same shape the sweep already keeps; restoring it adds display rows only, and anything that was
    // still queued or running when the plugin stopped comes back as cancelled, because it was interrupted.
    private void RestoreBatchHistory()
    {
        try
        {
            var path = BatchHistory.DefaultPath;
            var entries = BatchHistory.Load(path);
            if (entries.Count == 0)
            {
                return;
            }

            var restoredJobs = 0;
            foreach (var entry in entries)
            {
                foreach (var stored in entry.Jobs)
                {
                    if (_jobs.ContainsKey(stored.Id))
                    {
                        continue;
                    }

                    var job = new SyncJob
                    {
                        Id = stored.Id,
                        ItemId = stored.ItemId,
                        SubtitleIndex = stored.SubtitleIndex,
                        Mode = SyncJobMode.Normalize(stored.Mode),
                        BatchId = stored.BatchId ?? entry.BatchId,
                        BatchIndex = stored.BatchIndex,
                        BatchLabel = stored.BatchLabel ?? entry.Label,
                        Label = stored.Label,
                        Outcome = stored.Outcome,
                        OutputPath = stored.OutputPath,
                        Error = stored.Error,
                        ExtractionNote = stored.ExtractionNote,
                        Phase = stored.Phase,
                        Progress = stored.Progress,
                        CreatedAtUtc = stored.CreatedAtUtc,
                        FinishedAtUtc = stored.FinishedAtUtc
                    };

                    var status = Enum.TryParse<SyncJobStatus>(stored.Status, true, out var parsed)
                        ? parsed
                        : SyncJobStatus.Completed;
                    if (status is SyncJobStatus.Queued or SyncJobStatus.Running)
                    {
                        status = SyncJobStatus.Cancelled;
                        job.Phase = "Cancelled";
                        job.Outcome = "interrupted by a plugin restart";
                        job.FinishedAtUtc ??= DateTime.UtcNow;
                    }

                    job.Status = status;
                    if (_jobs.TryAdd(job.Id, job))
                    {
                        _historyOnlyJobs.Add(job.Id);
                        restoredJobs++;
                    }
                }
            }

            if (restoredJobs > 0)
            {
                PluginLog.Info(BatchHistory.DescribeRestore(entries.Count, restoredJobs, path));
            }
        }
        catch (Exception ex)
        {
            // A history that cannot be read is not worth a failed start-up.
            _logger.LogDebug(ex, "Could not restore the batch history");
        }
    }

    private List<BatchHistoryEntry> SnapshotBatchHistory()
    {
        var entries = new List<BatchHistoryEntry>();
        foreach (var group in _jobs.Values.Where(j => !string.IsNullOrEmpty(j.BatchId)).GroupBy(j => j.BatchId!))
        {
            entries.Add(new BatchHistoryEntry
            {
                BatchId = group.Key,
                Label = group.Select(j => j.BatchLabel).FirstOrDefault(l => !string.IsNullOrEmpty(l)),
                CreatedUtc = group.Min(j => j.CreatedAtUtc),
                Jobs = group.OrderBy(j => j.BatchIndex).Select(j => new BatchHistoryJob
                {
                    Id = j.Id,
                    ItemId = j.ItemId,
                    SubtitleIndex = j.SubtitleIndex,
                    Mode = j.Mode,
                    BatchId = j.BatchId,
                    BatchIndex = j.BatchIndex,
                    BatchLabel = j.BatchLabel,
                    Label = j.Label,
                    Status = j.Status.ToString(),
                    Outcome = j.Outcome,
                    OutputPath = j.OutputPath,
                    Error = j.Error,
                    ExtractionNote = j.ExtractionNote,
                    Phase = j.Phase,
                    Progress = j.Progress,
                    CreatedAtUtc = j.CreatedAtUtc,
                    FinishedAtUtc = j.FinishedAtUtc
                }).ToList()
            });
        }

        return entries;
    }

    // Called from the pump's tick. The cheap count signature decides whether anything changed at all,
    // and a minute has to have passed since the last write, so a running batch costs one small write a
    // minute rather than one per completed job.
    private void MaybePersistBatchHistory()
    {
        var terminal = 0;
        foreach (var job in _jobs.Values)
        {
            if (job.Status is SyncJobStatus.Completed or SyncJobStatus.Failed or SyncJobStatus.Cancelled)
            {
                terminal++;
            }
        }

        var signature = $"{_jobs.Count}|{terminal}";
        if (signature == _historySignature
            || DateTime.UtcNow - _historySavedUtc < TimeSpan.FromSeconds(60))
        {
            return;
        }

        _historySignature = signature;
        _historySavedUtc = DateTime.UtcNow;
        BatchHistory.Save(BatchHistory.DefaultPath, SnapshotBatchHistory());
    }

    // Called once per pump tick (about every 750 ms) and throttled to once a minute. The pump keeps
    // ticking while an engine run is silent, so a run reports even in the hour it spends inside one file.
    private void MaybeLogProgress()
    {
        if (DateTime.UtcNow - _lastProgressLineUtc < TimeSpan.FromSeconds(ProgressLineSeconds))
        {
            return;
        }

        var queued = CountQueued();
        var running = _jobs.Values.Count(j => j.Status == SyncJobStatus.Running);
        if (queued == 0 && running == 0)
        {
            return;
        }

        _lastProgressLineUtc = DateTime.UtcNow;

        int filesLeft;
        lock (_queueLock)
        {
            filesLeft = _runOrder
                .Where(j => j.Status == SyncJobStatus.Queued)
                .Select(j => _jobContexts.TryGetValue(j.Id, out var c) ? c.Video?.Path : null)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.Ordinal)
                .Count();
        }

        string? laneFile = null;
        TimeSpan? laneElapsed = null;
        var lane = _passInFlight.OrderBy(kvp => kvp.Value).FirstOrDefault();
        if (lane.Key is not null)
        {
            laneFile = Path.GetFileName(lane.Key);
            laneElapsed = DateTime.UtcNow - lane.Value;
        }

        LogPluginProgress(queued, running, filesLeft, laneFile, laneElapsed);
    }

    /// <summary>
    /// Kills everything: queued jobs (any batch) are cancelled and every running job's
    /// ffsubsync/ffmpeg processes are terminated. Backs the UI's Kill action, which is
    /// what the Cancel button turns into while processes are still running.
    /// </summary>
    /// <returns>How many jobs were dropped from the queue and how many runs were killed.</returns>
    public (int QueuedCancelled, int RunningKilled) KillAll()
    {
        var queuedCancelled = 0;
        List<SyncJob> cancelledAll;
        var cancelAllHold = System.Diagnostics.Stopwatch.StartNew();
        lock (_queueLock)
        {
            // Statuses only, for the reason the batch cancel gives: one log write per job inside this lock is
            // the enqueue path's wait (S40).
            cancelledAll = _runOrder.Where(j => j.Status == SyncJobStatus.Queued).ToList();
            foreach (var job in cancelledAll)
            {
                job.Status = SyncJobStatus.Cancelled;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.Phase = "Cancelled";
            }

            queuedCancelled += cancelledAll.Count;
        }

        cancelAllHold.Stop();
        LogLockHold("cancel-all", cancelAllHold.ElapsedMilliseconds);
        foreach (var job in cancelledAll)
        {
            LogPluginCancellation(job);
        }

        var runningKilled = 0;
        foreach (var kvp in _jobCancellation)
        {
            try
            {
                kvp.Value.Cancel();
                runningKilled++;
                if (_jobs.TryGetValue(kvp.Key, out var killedJob))
                {
                    LogPluginCancellation(killedJob);
                }
            }
            catch (ObjectDisposedException)
            {
                // The run finished between the check and the cancel.
            }
        }

        // Cancelling a token only helps processes that poll it. Kill the trees directly as
        // well: ffsubsync spawns ffmpeg, and both must be gone before the file handles are
        // released and the job is really finished.
        // The extraction lane reads a whole file in one pass and is not a job, so it needs its own
        // stop: measured before this, a kill left the lane reading 805.6 MB afterwards while
        // /SubSync/Active reported nothing running. The source is replaced immediately so the next
        // pass is not born cancelled.
        try
        {
            var stopping = _laneStop;
            _laneStop = new CancellationTokenSource();
            stopping.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // Shutdown already took it.
        }

        // What is reported is what actually exited, not what was asked to (B16): the count used to be the
        // larger of "process trees killed" and "run tokens cancelled", which is a number nobody measured - the
        // interface said "N stopped" for processes that were still alive. Each killed process is awaited on its
        // own handle with a deadline, so this is a wait rather than a poll. Shared with teardown (B14).
        var (processesAskedToStop, processesStopped) = KillChildProcesses();
        var survivors = _liveProcesses.Values.Count(p => !SafeHasExited(p));
        _logger.LogInformation(
            "Kill requested: {Queued} queued task(s) cancelled, {Running} run token(s) cancelled, {Killed} process tree(s) killed, {Survivors} still alive",
            queuedCancelled, runningKilled, processesStopped, survivors);

        // The plugin's own log, so a kill is verifiable from the one file that gets handed over for
        // debugging. Which phases the jobs were in matters: a job killed while extracting a subtitle
        // and a job killed while analysing speech fail in the same place otherwise.
        var runningPhases = _jobs.Values
            .Where(j => j.Status == SyncJobStatus.Running)
            .Select(j => $"{j.Id[..8]}={j.Phase} ({j.Label ?? j.BatchLabel ?? j.ItemId.ToString()[..8]})")
            .ToList();
        PluginLog.Info(
            $"KILL requested: {queuedCancelled} queued cancelled, {runningKilled} run token(s), "
            + $"{processesStopped} of {processesAskedToStop} process tree(s) stopped, {survivors} survivor(s)"
            + (runningPhases.Count == 0 ? string.Empty : " \u00b7 still reporting: " + string.Join(", ", runningPhases)));

        // The second number is processes that really exited - measured, not estimated from the tokens that
        // were cancelled or the trees that were signalled (B16).
        return (queuedCancelled, processesStopped);
    }

    /// <summary>True when a process has exited, without throwing when it is gone.</summary>
    /// <param name="process">Process to check.</param>
    /// <returns>True when it is no longer running.</returns>
    private static bool SafeHasExited(Process process)
    {
        try
        {
            return process.HasExited;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>How many child processes are tracked right now (used by the status endpoint).</summary>
    /// <returns>Number of live processes.</returns>
    private int LiveProcessCount() => _liveProcesses.Values.Count(p => !SafeHasExited(p));

    /// <summary>
    /// Gets a summary of what is executing right now, so the UI can tell the user whether
    /// anything is still running after a cancel.
    /// </summary>
    /// <returns>Running jobs and the queued count.</returns>
    public (IReadOnlyList<SyncJob> Running, int Queued) GetActive()
    {
        var running = _jobs.Values.Where(j => j.Status == SyncJobStatus.Running).OrderBy(j => j.CreatedAtUtc).ToList();
        var queued = _jobs.Values.Count(j => j.Status == SyncJobStatus.Queued);
        return (running, queued);
    }

    /// <summary>
    /// Gets all tracked jobs belonging to a batch, ordered by batch position.
    /// </summary>
    /// <param name="batchId">The batch identifier.</param>
    /// <returns>Ordered jobs of the batch.</returns>
    public IEnumerable<SyncJob> GetBatchJobs(string batchId)
    {
        return _jobs.Values
            .Where(j => j.BatchId == batchId)
            .OrderBy(j => j.BatchIndex);
    }

    /// <summary>
    /// Gets all batch ids seen so far, newest first.
    /// </summary>
    /// <returns>Distinct batch ids with their newest job creation time.</returns>
    public IEnumerable<(string BatchId, DateTime CreatedAt)> GetBatchIds()
    {
        return _jobs.Values
            .Where(j => j.BatchId is not null)
            .GroupBy(j => j.BatchId!)
            .Select(g => (BatchId: g.Key, CreatedAt: g.Min(j => j.CreatedAtUtc)))
            .OrderByDescending(g => g.CreatedAt);
    }

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
            if (!IsJobScratchDirectory(name) || _jobs.ContainsKey(name))
            {
                continue;
            }

            // Belt and braces: the recursive delete only ever runs on a path that resolved inside the root.
            if (!IsInsideRoot(rootFull, directory))
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

    /// <summary>
    /// True when this job's subtitle is already out of the media file, so the job has nothing to read
    /// and can start syncing straight away.
    /// </summary>
    /// <param name="job">Job being considered for a worker slot.</param>
    /// <returns>True when the subtitle text is available.</returns>
    private bool ExtractionReady(SyncJob job)
    {
        if (!_jobContexts.TryGetValue(job.Id, out var context))
        {
            return false;
        }

        // A sidecar subtitle is already a file of its own: there is nothing to extract.
        if (context.Stream.IsExternal)
        {
            return true;
        }

        var path = context.Video?.Path;
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var key = ExtractedKeyOf(path, context.Ordinal);
        if (_extractedReady.ContainsKey(key))
        {
            return true;
        }

        // The lane is reading this file right now, so the tracks it has not handed over yet are on
        // their way. Starting the job anyway is what made one file be read once per worker: measured
        // on the slow-storage profile, the lane read a 50-track episode (970.4 MB, 3 991 reads,
        // 473 472 ms) and four jobs then each started their own "Extracting N subtitles from the
        // Matroska index in one pass" over the same file, so the episode cost gigabytes of reads
        // instead of one pass. Waiting costs the job nothing - it could not have started syncing
        // before its subtitle existed - and the lane picks up the remaining ordinals the moment its
        // current pass ends. The escape hatch below still lets jobs extract for themselves when no
        // lane is running or the lane has gone quiet.
        if (_passInFlight.ContainsKey(path))
        {
            return false;
        }

        if (SubtitleCache.TryGet(path, context.Ordinal.ToString(CultureInfo.InvariantCulture), out var text)
            && text.Length > 0)
        {
            _extractedReady[key] = 0;
            return true;
        }

        // The lane ran a pass over this file and came back without this track: a picture track with no
        // text, or an empty one. Holding the job back forever is what made the run look stuck (32
        // queued, one running, limit 4, because only one job per file was ever ready); letting it start
        // means it reports its own reason instead of sitting in the queue for ever.
        if (_extractTried.ContainsKey(key))
        {
            return true;
        }

        return false;
    }

    /// <summary>Key for "this track of this file has been extracted".</summary>
    /// <param name="videoPath">Media file.</param>
    /// <param name="ordinal">Subtitle ordinal within the file.</param>
    /// <returns>Lookup key.</returns>
    private static string ExtractedKeyOf(string videoPath, int ordinal) => videoPath + "\u0000" + ordinal;

    /// <summary>
    /// Applies a settings change to the running scheduler at once.
    /// </summary>
    /// <remarks>
    /// The settings page stores through the API and expects what it saved to be in force, so the scheduler is
    /// woken here rather than on the next run: the sync worker limit is read per planning pass, and the
    /// extraction lanes' width is recomputed when the extractor is woken (F13). An increase applies within a
    /// second; a decrease applies as the running extractions finish, because a lane that is mid-file is not
    /// stopped to satisfy a number.
    /// </remarks>
    public void ApplySettingsNow()
    {
        WakePump();
        PluginLog.Info($"settings applied: workers={ConfiguredWorkerLimit} lanes={ConfiguredLaneLimit}");
    }

    /// <summary>
    /// Gets how many extraction lanes the current settings ask for, without starting any.
    /// </summary>
    /// <remarks>Half the worker count, bounded by <see cref="MaxExtractionLanes"/> - the number the UI states.</remarks>
    public int ConfiguredLaneLimit => Math.Clamp(
        (Services.SettingsSource.Current()?.ParallelWorkers ?? DefaultParallelWorkers) / 2, 1, MaxExtractionLanes);

    /// <summary>Wakes the extraction lane, starting it if it is not running.</summary>
    private void WakeExtractor()
    {
        // Read through the settings source, not the plugin's in-memory copy: the page stores settings through
        // the API, and a hand-edited config.xml changes nothing else. Half the worker count, because a lane
        // reads a file while the workers drive the engine on files already read (F13).
        var configured = Services.SettingsSource.Current()?.ParallelWorkers ?? DefaultParallelWorkers;
        var wanted = Math.Clamp(configured / 2, 1, MaxExtractionLanes);

        lock (_queueLock)
        {
            _laneTasks.RemoveAll(task => task.IsCompleted);
            while (_laneTasks.Count < wanted)
            {
                _laneTasks.Add(Task.Run(ExtractLaneAsync));
            }
        }

        try { _extractWake.Release(); }
        catch (SemaphoreFullException) { /* already signalled */ }
    }

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
        return long.TryParse(raw, out var parsed) && parsed >= 0 ? parsed : 250;
    }

    private const int MaxExtractionLanes = 3;

    /// <summary>
    /// How many stderr lines of an extraction are kept for judging it. ffmpeg reports a truncation when it
    /// reaches the end of the input, so the tail holds it; the bound keeps a long demux's progress lines from
    /// growing the job's memory.
    /// </summary>
    private const int ExtractionStderrTailLines = 200;

    /// <summary>True while at least one extraction lane is running.</summary>
    private bool LaneAlive
    {
        get
        {
            lock (_queueLock)
            {
                return _laneTasks.Exists(task => !task.IsCompleted);
            }
        }
    }

    /// <summary>
    /// Keeps the queue's subtitles extracted.
    ///
    /// Pulls one media file at a time, extracts every queued subtitle of it in a single pass, stores
    /// each result as it arrives and lets the scheduler start those jobs. Runs for the life of the
    /// plugin and is signalled by the enqueue path and by the planner.
    /// </summary>
    private async Task ExtractLaneAsync()
    {
        PluginLog.Info("extract lane: started");
        while (!_disposing)
        {
            var work = NextFileToExtract();
            if (work is null)
            {
                try
                {
                    await _extractWake.WaitAsync(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
                }
                catch (ObjectDisposedException)
                {
                    return;
                }

                continue;
            }

            var (videoPath, ordinals) = work.Value;
            if (!_passInFlight.TryAdd(videoPath, DateTime.UtcNow))
            {
                continue;
            }

            // This pass's own view of "everything is being stopped". A Kill cancels it; the token the
            // extractor receives is the only reason a `Kill` used to leave the reader going.
            var passStop = _laneStop;

            try
            {
                var results = new Dictionary<int, string>();
                var stats = new MkvExtractionStats();
                foreach (var ordinal in ordinals)
                {
                    _extractTried.TryRemove(ExtractedKeyOf(videoPath, ordinal), out _);
                }

                var ok = false;
                var reason = string.Empty;
                ok = await Task.Factory.StartNew(
                    () => MkvSubtitleExtractor.TryExtractMany(
                        videoPath,
                        ordinals,
                        out results,
                        out reason,
                        out stats,
                        passStop.Token,
                        // Hand each subtitle over the moment the pass has read its last line, not when
                        // the whole file is done: that job starts syncing straight away, which is the
                        // difference between a language waiting for the other twenty-nine and it going
                        // as soon as it is out. The pass keeps reading for the rest.
                        (ordinal, text) =>
                        {
                            if (text.Length == 0)
                            {
                                return;
                            }

                            SubtitleCache.Store(videoPath, ordinal.ToString(CultureInfo.InvariantCulture), text);
                            _extractedReady[ExtractedKeyOf(videoPath, ordinal)] = 0;
                            WakePump();
                        }),
                    passStop.Token,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);

                // A cancelled pass is not a failed one. The Matroska reader reports cancellation as an
                // ordinary `false` ("cancelled"), and treating that as "this track holds no text" marks
                // every track the pass never reached as unextractable until the next restart — the jobs
                // for them then start, find nothing and fail. A Kill stops the pass; the tracks stay
                // queued and the next pass reads them.
                if (passStop.IsCancellationRequested)
                {
                    throw new OperationCanceledException(passStop.Token);
                }

                foreach (var pair in results)
                {
                    if (pair.Value.Length == 0)
                    {
                        continue;
                    }

                    // Already stored by the callback for most of them; this covers a track the pass
                    // produced without passing its last cue (an unindexed track, or a truncated file).
                    SubtitleCache.Store(videoPath, pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value);
                    _extractedReady[ExtractedKeyOf(videoPath, pair.Key)] = 0;
                }

                SubtitleCache.Prune();
                PluginLog.Info(
                    $"extract lane: {Path.GetFileName(videoPath)} -> {results.Count}/{ordinals.Count} subtitle(s), "
                    + $"{stats.BytesRead / 1e6:0.0} MB, {stats.ReadCalls} reads, {stats.TotalMs:0} ms, ok={ok}, "
                    + $"reason={reason} "
                    + $"prefetched={stats.PrefetchedRanges} ranges/{stats.PrefetchedBytes / 1e6:0.0} MB "
                    + $"unused={stats.PrefetchedUnusedRanges} ranges/{stats.PrefetchedUnusedBytes / 1e6:0.0} MB "
                    + $"bytesTwice={stats.BytesReadTwice / 1e6:0.00} MB memoryReads={stats.MemoryServedReads} "
                    + $"plan={stats.Route} expected={stats.PlanExpectedBytes / 1e6:0.00} MB/{stats.PlanExpectedCalls} reads "
                    + $"missed={stats.PlanMissed} indexedMisses={stats.IndexedMisses} walked={stats.WalkedClusters} "
                    + $"storage={stats.MeasuredMsPerRead:0.00} ms/read {stats.MeasuredMbPerSecond:0.0} MB/s "
                    + $"({DescribeExtraction(stats.Method)})");

                foreach (var ordinal in ordinals)
                {
                    if (!results.ContainsKey(ordinal) || results[ordinal].Length == 0)
                    {
                        // Nothing in this track to sync (a picture track, or an empty one). Recording it
                        // stops the lane from asking again on every pass.
                        _extractTried[ExtractedKeyOf(videoPath, ordinal)] = 0;
                    }
                }

                _lastPassFinishedUtc = DateTime.UtcNow;
                if (results.Count > 0)
                {
                    WakePump();
                }
            }
            catch (OperationCanceledException)
            {
                // A Kill stops this pass. Whatever the pass handed over before it stopped is already in
                // the subtitle cache and is reused; the rest stays queued and the next pass reads on,
                // because a cancelled pass records nothing about the tracks it never reached.
                PluginLog.Info(
                    $"extract lane: pass on {Path.GetFileName(videoPath)} stopped (killed by the user) "
                    + $"with {ordinals.Count} subtitle(s) still owed");
                _lastPassFinishedUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Extraction lane failed for {Video}", videoPath);
            }
            finally
            {
                _passInFlight.TryRemove(videoPath, out _);
            }
        }

        PluginLog.Info("extract lane: stopped");
    }

    /// <summary>
    /// Picks the next media file with queued subtitles that are not extracted yet, and the ordinals it
    /// still owes. Skips files a pass is already running on, and sidecar subtitles, which need nothing.
    /// </summary>
    /// <returns>The file and its missing ordinals, or null when there is nothing to do.</returns>
    private (string VideoPath, List<int> Ordinals)? NextFileToExtract()
    {
        const int MaxTracksPerPass = 48;
        var byFile = new Dictionary<string, List<int>>(StringComparer.Ordinal);
        lock (_queueLock)
        {
            foreach (var job in _runOrder)
            {
                if (job.Status != SyncJobStatus.Queued
                    || !_jobContexts.TryGetValue(job.Id, out var context)
                    || context.Stream.IsExternal)
                {
                    continue;
                }

                var path = context.Video?.Path;
                if (string.IsNullOrEmpty(path) || _passInFlight.ContainsKey(path))
                {
                    continue;
                }

                if (!byFile.TryGetValue(path, out var wanted))
                {
                    wanted = new List<int>();
                    byFile[path] = wanted;
                }

                if (!wanted.Contains(context.Ordinal) && wanted.Count < MaxTracksPerPass)
                {
                    wanted.Add(context.Ordinal);
                }

                // The ruler comes out of the same pass. ffsubsync aligns each subtitle against another
                // subtitle track of the same file, and a job that finds the reference missing reads the
                // file again for it - measured on a real episode, four such passes of ~330 MB each for
                // one file. Asking for it here means one read produces both the languages and the ruler.
                foreach (var referenceOrdinal in ReferenceOrdinalsFor(path, context.Ordinal, context.Video))
                {
                    if (!wanted.Contains(referenceOrdinal) && wanted.Count < MaxTracksPerPass)
                    {
                        wanted.Add(referenceOrdinal);
                    }
                }
            }
        }

        foreach (var (path, wanted) in byFile)
        {
            var missing = wanted
                .Where(o => !_extractedReady.ContainsKey(ExtractedKeyOf(path, o))
                    && !_extractTried.ContainsKey(ExtractedKeyOf(path, o)))
                .ToList();
            if (missing.Count > 0)
            {
                return (path, missing);
            }
        }

        return null;
    }

    /// <summary>
    /// Track ordinals a job may align against, which the extraction lane wants produced as well.
    ///
    /// The choice itself belongs to the job (it must not align a track against that same track), so
    /// this asks the same picker the job path uses, with Jellyfin's stream list instead of the ffprobe
    /// list the job builds. The two agree on codec and order in practice; when they do not, the job
    /// extracts the reference for itself exactly as it did before, so a miss costs a read and never
    /// correctness. Memoised: the planner asks twice a second for every queued job.
    /// </summary>
    /// <param name="videoPath">Media file, part of the memo key.</param>
    /// <param name="targetOrdinal">Subtitle this job is fixing.</param>
    /// <param name="video">The library item.</param>
    /// <returns>Ordinals to extract, empty when the picker cannot decide.</returns>
    private IEnumerable<int> ReferenceOrdinalsFor(string videoPath, int targetOrdinal, Video? video)
    {
        var key = ExtractedKeyOf(videoPath, targetOrdinal) + "\u0000ref";
        if (_referenceOrdinals.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var ordinals = new List<int>();
        try
        {
            if (video is null)
            {
                return ordinals;
            }

            var streams = video.GetMediaSources(true)
                .SelectMany(source => source.MediaStreams)
                .Where(stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !stream.IsExternal)
                .OrderBy(stream => stream.Index)
                .ToList();

            if (streams.Count > 0)
            {
                var spec = SelectReferenceStream(
                    true,
                    streams.Select(stream => stream.Codec).ToList(),
                    targetOrdinal,
                    streams.Select(stream => stream.IsForced).ToList());
                var ordinal = SubtitleStreamOrdinal(spec);
                if (ordinal >= 0)
                {
                    ordinals.Add(ordinal);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Could not work out the reference track for {Video}; the job will extract it", videoPath);
        }

        return _referenceOrdinals.GetOrAdd(key, ordinals);
    }

    private bool SpeechIsCached(SyncJob job)
    {
        try
        {
            if (!_jobContexts.TryGetValue(job.Id, out var ctx))
            {
                return false;
            }

            // The answer belongs to the media file, and the scheduler asks for it for every queued job
            // on every planning pass - while holding the queue lock the enqueue path waits on. Reading
            // it from storage each time cost tens of seconds per queued task on a share (240 tasks took
            // 105-424 s to queue, the slowest single task 32-40 s, almost all of it "log=" time spent
            // waiting for that lock). The answer cannot change except by a finished analysis, which is
            // itself a filesystem event of the same file, so a few seconds of trust is enough.
            var path = ctx.Video.Path;
            var now = DateTime.UtcNow;

            lock (_speechCachedGate)
            {
                if (_speechCachedMemo.TryGetValue(path, out var memo) && now - memo.At < SpeechCachedTtl)
                {
                    return memo.Cached;
                }
            }

            var key = SpeechCache.KeyFor(
                path,
                AudioReferenceVad + "|audio",
                EngineIdentity());
            var cached = SpeechCache.TryGet(key) is not null;

            lock (_speechCachedGate)
            {
                _speechCachedMemo[path] = (cached, now);
            }

            return cached;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>Reference hint used for the cache key when only the job is known.</summary>
    private string SpeechCacheReferenceHint(SyncJob job) =>
        _jobContexts.TryGetValue(job.Id, out var ctx) && ctx.Stream.IsExternal ? "auto" : "embedded";

    /// <summary>Identity of the engine that shapes a speech signal.</summary>
    private string EngineIdentity() => string.Join(
        "|",
        BundledFfSubSyncVersion,
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");

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
    /// Checks whether a folder can be written to, so a library the Jellyfin user cannot write to
    /// fails once with a clear reason instead of reporting an access error for every subtitle in
    /// it.
    ///
    /// This happens on read-only mounts, on shares that map a different owner, and on folders the
    /// container user cannot write. Detecting it in advance turns "Access to the path is denied"
    /// repeated a hundred times into one sentence naming the folder.
    /// </summary>
    /// <param name="directory">Folder the synced subtitle would be written to.</param>
    /// <param name="reason">Why it cannot be written, when it cannot.</param>
    /// <returns>True when a file could be created there.</returns>
    public static bool CanWriteTo(string directory, out string reason)
    {
        try
        {
            Directory.CreateDirectory(directory);
            var probe = Path.Combine(directory, ".subsync-write-probe-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "probe");
            File.Delete(probe);
            reason = string.Empty;
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            reason = "the Jellyfin user has no write permission there (the folder may also be mounted read-only)";
            return false;
        }
        catch (IOException ex)
        {
            // The exception message is the useful part here: "Read-only file system" and
            // "Permission denied" mean different fixes.
            reason = "the folder could not be written to: " + ex.Message;
            return false;
        }
        catch (NotSupportedException)
        {
            reason = "the path is not a writable folder";
            return false;
        }
    }

    /// <summary>
    /// Throws a message that explains a folder the plugin cannot write to, and states plainly that
    /// nothing was changed.
    /// </summary>
    /// <param name="directory">Folder to check.</param>
    private static void RequireWritable(string directory)
    {
        if (!CanWriteTo(directory, out var reason))
        {
            throw new InvalidOperationException(
                $"Cannot write the synced subtitle to '{directory}': {reason}. "
                + "Nothing was changed and the original subtitle is untouched. "
                + "Fix the folder's permissions for the Jellyfin user (or how the library is mounted) and run again.");
        }
    }

    /// <summary>
    /// Reads the "done/total" counters out of an extraction progress line.
    ///
    /// The extractor reports "reading subtitle 128/326 · 1.3 MB, 341 reads · …"; the fraction is
    /// what lets the progress bar follow real work instead of standing still.
    /// </summary>
    /// <param name="line">Progress text from an extractor.</param>
    /// <returns>The completed fraction in 0..1, or null when the line has no counters.</returns>
    public static double? ExtractionFraction(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return null;
        }

        var match = System.Text.RegularExpressions.Regex.Match(line, @"(\d+)\s*/\s*(\d+)");
        if (!match.Success
            || !double.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var done)
            || !double.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var total)
            || total <= 0)
        {
            return null;
        }

        return Math.Clamp(done / total, 0.0, 1.0);
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
    /// Whether a subtitle ruler's answer and the film's own audio disagree about how far the subtitles must move.
    /// </summary>
    /// <remarks>
    /// A pure function on purpose: the numbers are the decision, and a check has to be able to disagree with it.
    /// Measured 2026-09-15 on the rig - the same ruler back-to-back agrees with itself to a hundredth of a second,
    /// where the other-cut ruler's 24 170 ms against the audio's own answer is a different film's timeline.
    /// </remarks>
    /// <param name="rulerShiftMs">The shift the subtitle ruler asked for.</param>
    /// <param name="audioShiftMs">The shift the film's own audio asked for, on the same subtitle.</param>
    /// <param name="referenceCeilingMs">The configured limit on a subtitle ruler's demanded shift.</param>
    /// <returns>True when the ruler loses to the audio.</returns>
    internal static bool RulersDisagree(long rulerShiftMs, long audioShiftMs, double referenceCeilingMs)
        => Math.Abs(audioShiftMs - rulerShiftMs)
           > Math.Max(1.0, referenceCeilingMs) * SubtitleReferenceAudioAgreementFraction;

    /// <summary>
    /// Whether a subtitle ruler's cues moved too unevenly for it to be this film's own timeline.
    /// </summary>
    /// <remarks>
    /// Kept as a pure function because the numbers are the whole decision and a check has to be able to disagree
    /// with it: on 2026-09-15 the real sibling track of a file measured a spread of 0 ms and the same track from
    /// a 2 % longer cut measured 27 760 ms, at a 30 s reference ceiling (so a 7 500 ms bar).
    /// </remarks>
    /// <param name="change">What the sync changed, cue by cue.</param>
    /// <param name="referenceCeilingMs">The configured limit on a subtitle ruler's demanded shift, in milliseconds.</param>
    /// <returns>True when the ruler should be discarded and the audio used instead.</returns>
    internal static bool RulerSpreadTooWide(SyncChange change, double referenceCeilingMs)
        => change.SpreadMs > Math.Max(1.0, referenceCeilingMs) * SubtitleReferenceSpreadFraction;

    /// <summary>
    /// How much of the subtitle-reference offset ceiling the per-cue spread may reach before that ruler is
    /// refused as "not the same cut".
    /// </summary>
    /// <remarks>
    /// A quarter of <c>MaxSubtitleReferenceOffsetSeconds</c>, so both numbers move together and the check has no
    /// constant of its own: at the 30 s default a spread over 7,5 s is refused. Measured on 2026-09-15, the real
    /// sibling track of a file measured a spread of 0,00 s and the same track from a 2 % longer cut measured
    /// 27,76 s, so the line sits between two observations rather than in the middle of one. A language's own
    /// timing differences are fractions of a second, and a genuinely rescaled framerate mismatch is rescaled by
    /// the plugin *before* the engine sees it - which is why a wide spread here means the ruler, not the timing.
    /// </remarks>
    public const double SubtitleReferenceSpreadFraction = 0.25;

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

    /// <summary>
    /// How far apart a subtitle ruler's answer and the film's own audio may be before the ruler loses.
    /// </summary>
    /// <remarks>
    /// A tenth of the reference ceiling - 3 s at the default. Measured on 2026-09-15: two alignments of the same
    /// file against the *same* ruler differ by hundredths of a second, and the rig's other-cut ruler measured
    /// 24 170 ms against the audio's own answer, so the line sits between a measured agreement and a measured
    /// disagreement rather than in the middle of one.
    /// </remarks>
    public const double SubtitleReferenceAudioAgreementFraction = 0.1;

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

    /// <summary>
    /// Gets how many jobs may run at once with the current settings. Reported to the UI so the
    /// effective parallelism is visible instead of inferred.
    /// </summary>
    public int EffectiveWorkerLimit =>
        NormalizeWorkers(Services.SettingsSource.Current()?.ParallelWorkers ?? DefaultParallelWorkers);

    /// <summary>
    /// The worker count exactly as configured, without falling back to the default.
    ///
    /// Exposed so the interface can show it beside the limit actually in force: a report of
    /// "4/4 workers" with 8 configured means one of the two is not what the other thinks, and
    /// that is only visible when both are stated.
    /// </summary>
    public int ConfiguredWorkerLimit => Services.SettingsSource.Current()?.ParallelWorkers ?? -1;

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
    /// Gets a sync job by its ID.
    /// </summary>
    /// <param name="jobId">The job identifier.</param>
    /// <returns>The sync job, or null if not found.</returns>
    public SyncJob? GetJob(string jobId)
    {
        return _jobs.TryGetValue(jobId, out var job) ? job : null;
    }

    /// <summary>
    /// Gets all sync jobs.
    /// </summary>
    /// <returns>All tracked sync jobs.</returns>
    public IEnumerable<SyncJob> GetAllJobs()
    {
        return _jobs.Values;
    }

    /// <summary>
    /// Runs a full-library sweep: queues one sync job per external subtitle track
    /// that has no synced output yet, honoring the persistent skip/fail cache,
    /// then waits for the queued batch to finish (through the normal FIFO pump).
    /// </summary>
    /// <param name="progress">Sweep progress, 0..1.</param>
    /// <param name="cancellationToken">Cancels queued-but-unstarted tasks.</param>
    /// <returns>Counts describing the run.</returns>
    public async Task<SweepResult> SweepLibraryAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var result = new SweepResult();
        var config = Services.SettingsSource.Current();
        var failStreakLimit = Math.Max(1, config?.SweepFailStreakLimit ?? 3);
        var maxItems = Math.Max(1, config?.SweepMaxItemsPerRun ?? 500);

        var root = _libraryManager.RootFolder;
        var videos = root?.GetRecursiveChildren().OfType<Video>().ToList() ?? new List<Video>();
        result.ScannedItems = videos.Count;
        progress.Report(0.02);

        var tasks = new List<(Guid ItemId, int SubtitleIndex, string? Title)>();
        var seenTracks = new HashSet<(Guid, int)>();

        for (var i = 0; i < videos.Count; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var video = videos[i];

            if (tasks.Count >= maxItems)
            {
                break;
            }

            var tracks = ListSubtitles(video.Id);
            if (tracks is null)
            {
                continue;
            }

            foreach (var track in tracks)
            {
                if (tasks.Count >= maxItems)
                {
                    break;
                }

                if (!track.IsExternal || string.IsNullOrWhiteSpace(track.ExternalPath))
                {
                    continue; // sweep targets external subtitle files only
                }

                if (!seenTracks.Add((video.Id, track.Index)))
                {
                    continue;
                }

                if (track.HasSyncedVersion)
                {
                    result.SkippedOther++;
                    continue;
                }

                var path = track.ExternalPath;
                if (!File.Exists(path))
                {
                    result.SkippedOther++;
                    continue;
                }

                var hash = SweepState.HashFile(path);
                var entry = _sweepState.Value.Get(path);

                if (entry is not null
                    && string.Equals(entry.SourceHash, hash, StringComparison.Ordinal)
                    && entry.OutputPath is not null
                    && File.Exists(entry.OutputPath)
                    && entry.FailStreak == 0)
                {
                    // Same source content, synced output still on disk.
                    result.SkippedCached++;
                    continue;
                }

                if (entry is not null
                    && string.Equals(entry.SourceHash, hash, StringComparison.Ordinal)
                    && entry.FailStreak >= failStreakLimit)
                {
                    result.SkippedFailed++;
                    continue;
                }

                tasks.Add((video.Id, track.Index, $"{video.Name} — {track.Title}"));
            }

            if (i % 50 == 0 || i == videos.Count - 1)
            {
                progress.Report(0.02 + (0.08 * (i + 1) / Math.Max(1, videos.Count)));
            }
        }

        result.CandidatesEnqueued = tasks.Count;
        if (tasks.Count == 0)
        {
            progress.Report(1.0);
            return result;
        }

        var batchLabel = $"Library sweep — {DateTime.Now:yyyy-MM-dd HH:mm}";
        var created = CreateBatch(batchLabel, tasks);
        var batchId = created.Jobs.FirstOrDefault()?.BatchId ?? created.BatchId;
        if (created.Jobs.Count == 0)
        {
            // Every task was already queued or running (D9): there is nothing new for this sweep to run, which is
            // not a failure and must not be reported as one.
            if (created.AlreadyQueued.Count > 0)
            {
                PluginLog.Info($"sweep: nothing new — {created.AlreadyQueued.Count} task(s) were already queued");
                result.FailedOrCancelled = 0;
            }
            else
            {
                result.FailedOrCancelled = tasks.Count;
            }

            progress.Report(1.0);
            return result;
        }

        progress.Report(0.12);

        // Wait for the batch through the same FIFO pump used by the UI, so the
        // sweep never runs parallel to user-triggered syncs.
        while (!cancellationToken.IsCancellationRequested)
        {
            var jobs = GetBatchJobs(batchId).ToList();
            if (jobs.Count == 0)
            {
                break;
            }

            var done = jobs.Count(j => j.Status is SyncJobStatus.Completed or SyncJobStatus.Failed or SyncJobStatus.Cancelled);
            result.Completed = jobs.Count(j => j.Status == SyncJobStatus.Completed);
            result.FailedOrCancelled = done - result.Completed;
            progress.Report(0.12 + (0.88 * done / Math.Max(1, jobs.Count)));

            if (done >= jobs.Count)
            {
                break;
            }

            await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            CancelBatch(batchId);
        }

        // The sweep's records are written in batches; this is the one point the run is over either way, so
        // whatever is still in memory lands here - including on a canceled run, where everything already
        // synced still deserves to be skipped next time.
        _sweepState.Value.Flush();

        _logger.LogInformation(
            "Library sweep finished: {Scanned} items scanned, {Enqueued} queued, {Cached} skipped (synced), {FailedStreak} skipped (fail streak), {Ok} ok, {Bad} failed/cancelled",
            result.ScannedItems, result.CandidatesEnqueued, result.SkippedCached, result.SkippedFailed,
            result.Completed, result.FailedOrCancelled);

        return result;
    }

    private void RecordSweepOutcome(SyncJob job, bool ok, string? outputPath, string? error)
    {
        try
        {
            if (!_jobContexts.TryGetValue(job.Id, out var ctx))
            {
                return;
            }

            if (!ctx.Stream.IsExternal || string.IsNullOrWhiteSpace(ctx.Stream.Path))
            {
                return; // skip/fail cache tracks external subtitle files only
            }

            _sweepState.Value.Record(ctx.Stream.Path, ok, outputPath, error);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to update sweep cache for job {JobId}", job.Id);
        }
    }

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
    /// <exception cref="InvalidOperationException">The stream could not be mapped.</exception>
    private async Task<(int ContainerIndex, int SubtitleOrdinal, List<string> Codecs)> ResolveContainerSubtitleIndexAsync(Video video, MediaBrowser.Model.Entities.MediaStream target)
    {
        var ffmpegPath = ResolveFfmpegPath();
        var (_, stderr) = await RunProcessArgumentListAsync(ffmpegPath, new[] { "-i", video.Path }, null, CancellationToken.None).ConfigureAwait(false);
        var containerSubs = ParseProbeSubtitleIndexes(stderr);
        var subtitleCodecs = ParseProbeSubtitleCodecs(stderr);

        var mediaSources = video.GetMediaSources(true);
        var jellyfinStreams = (mediaSources.Count > 0 ? mediaSources[0] : null)?.MediaStreams
            ?? new List<MediaBrowser.Model.Entities.MediaStream>();
        var jellyfinEmbedded = jellyfinStreams
            .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !s.IsExternal)
            .ToList();

        // The ordinal comes from the same definition the enqueue path uses, so the track a job was
        // queued for and the track this resolver finds cannot drift apart.
        var pos = EmbeddedSubtitleOrdinal(jellyfinStreams, target);

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
            if (match.Success && int.TryParse(match.Groups[1].Value, out var idx))
            {
                result.Add(idx);
            }
        }

        return result;
    }

    /// <summary>
    /// Whether an alignment holds up against the film's own audio.
    /// </summary>
    /// <remarks>
    /// The audio is the film: a subtitle that has been put in the right place needs almost nothing further to line up
    /// with the speech, while a stretch applied to a differently cut subtitle - or a wide allowance that locked onto
    /// the wrong part of the audio - still asks for a large shift. Ten seconds, or half a percent of the runtime, is
    /// the room left for a genuine difference in intro or outro length.
    /// </remarks>
    /// <param name="ratio">Time ratio the audio alignment asked for on the result (1.0 = none).</param>
    /// <param name="shiftMs">Shift the audio alignment asked for on the result.</param>
    /// <param name="videoSeconds">The file's duration.</param>
    /// <returns>True when the result holds.</returns>
    internal static bool AlignmentHoldsAgainstAudio(double ratio, long shiftMs, double videoSeconds)
    {
        if (Math.Abs(ratio - 1.0) > 0.005)
        {
            return false;
        }

        var ceiling = Math.Max(10.0, videoSeconds * 0.005);
        return Math.Abs(shiftMs) <= ceiling * 1000.0;
    }

    /// <summary>
    /// Whether the subtitle being synced is the one that sits off the file's own timeline.
    /// </summary>
    /// <remarks>
    /// Both a subtitle timed for another playback speed and one from a longer cut show up as a span that is a few
    /// percent away from the reference's, so the references alone cannot say which of the two is the odd one out.
    /// The file's duration can: a subtitle spans the film it was timed for, so the side that disagrees with the
    /// duration is the one to correct. Getting this backwards would rescale a correct subtitle onto a mis-timed
    /// ruler, and every subtitle of the file shares that ruler in a bulk run.
    /// </remarks>
    /// <param name="targetSpan">Span of the subtitle being synced, in seconds.</param>
    /// <param name="referenceSpan">Span of the reference, in seconds.</param>
    /// <param name="videoSeconds">The file's duration, in seconds.</param>
    /// <returns>True when the subtitle is the side to rescale; false when the reference is, or when both or neither are.</returns>
    internal static bool IsTargetOffTheVideo(double targetSpan, double referenceSpan, double videoSeconds)
    {
        if (videoSeconds <= 60)
        {
            return true;
        }

        var targetOnVideo = Math.Abs((targetSpan / videoSeconds) - 1.0) <= 0.03;
        var referenceOnVideo = Math.Abs((referenceSpan / videoSeconds) - 1.0) <= 0.03;
        if (!referenceOnVideo && !targetOnVideo)
        {
            // Neither span matches the file: not a case this rule can settle, so the pair rule and the alignment
            // guards below decide, as before.
            return true;
        }

        return !targetOnVideo;
    }

    /// <summary>
    /// Reads one SRT timestamp ("00:01:02,345") as milliseconds.
    /// </summary>
    /// <param name="text">The timestamp, optionally followed by cue coordinates.</param>
    /// <param name="ms">The parsed value.</param>
    /// <returns>True when it parsed.</returns>
    internal static bool TryParseSrtTime(string text, out double ms)
    {
        ms = 0;
        var token = text.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (token is null)
        {
            return false;
        }

        var parts = token.Split(':', ',', '.');
        if (parts.Length < 4
            || !int.TryParse(parts[0], out var h)
            || !int.TryParse(parts[1], out var m)
            || !int.TryParse(parts[2], out var s)
            || !int.TryParse(parts[3], out var frac))
        {
            return false;
        }

        // One, two or three fraction digits all appear in the wild (see ParseSrtCueStarts).
        var fraction = frac / Math.Pow(10, parts[3].Length);
        ms = ((h * 3600.0) + (m * 60.0) + s + fraction) * 1000.0;
        return true;
    }

    /// <summary>
    /// Writes a copy of a subtitle scaled onto the reference subtitle's time base, when the two spans are a
    /// framerate pair apart.
    /// </summary>
    /// <remarks>
    /// A subtitle reference cannot fix a framerate mismatch by itself: it is another subtitle, so the engine has
    /// no frame rate to read and can only fit a shift. A PAL-timed target then comes out as a pure shift of
    /// roughly half the film's drift - measured on a user's file at 111.9 s over 96 minutes - which the reference
    /// ceiling refuses, so the sync failed on exactly the file the framerate option was turned on for. Both spans
    /// are known here, so the plugin does the rescale the reference cannot do and leaves the aligner the small
    /// shift it is good at. A span difference that is not a pair is a different cut: nothing is rescaled.
    /// </remarks>
    /// <param name="targetPath">The subtitle being synced.</param>
    /// <param name="referencePath">The reference subtitle.</param>
    /// <param name="videoDuration">The file's duration, which settles which side is off the video's timeline.</param>
    /// <param name="tempDir">Directory to write the copy into.</param>
    /// <param name="job">The job, for the log.</param>
    /// <returns>The path of the rescaled copy, or null when nothing should change.</returns>
    private string? RescaleOntoReferenceSpan(
        string targetPath,
        string referencePath,
        TimeSpan videoDuration,
        string tempDir,
        SyncJob job)
    {
        var target = ParseSrtCueStarts(targetPath);
        var reference = ParseSrtCueStarts(referencePath);
        if (target is null || reference is null || target.Count < 3 || reference.Count < 3)
        {
            return null;
        }

        var targetSpan = target[^1] - target[0];
        var referenceSpan = reference[^1] - reference[0];
        if (targetSpan <= 60 || referenceSpan <= 60)
        {
            return null;
        }

        // Which side is off the video's timeline? A subtitle spans the film it was timed for, so a span a few
        // percent away from the file's duration is a subtitle timed for a different playback speed. If the
        // *reference* is that one, rescaling the target onto it would drag a correct subtitle onto a mis-timed
        // ruler - and in bulk that would happen to every subtitle of the file, because they share the reference.
        if (videoDuration > TimeSpan.FromSeconds(60))
        {
            var videoSeconds = videoDuration.TotalSeconds;
            var referenceOnVideo = Math.Abs((referenceSpan / videoSeconds) - 1.0) <= 0.03;
            if (IsTargetOffTheVideo(targetSpan, referenceSpan, videoSeconds))
            {
                // The ordinary case: the subtitle is the one that is off.
            }
            else if (!referenceOnVideo)
            {
                PluginLog.Info(
                    $"[{job.Id}] framerate: the reference spans {referenceSpan:0.0} s of the file's {videoSeconds:0.0} s "
                    + $"(the subtitle's own span, {targetSpan:0.0} s, matches the file), so the reference is the odd one "
                    + "out \u2014 no rescale; a shift from a reference this far off the video is refused as before");
                return null;
            }
        }

        var scale = referenceSpan / targetSpan;
        if (Math.Abs(scale - 1.0) <= 0.005)
        {
            return null;
        }

        if (!KnownFramerateRatios.Any(known => Math.Abs(scale - known) <= 0.003))
        {
            PluginLog.Info(
                $"[{job.Id}] framerate: the subtitle's span is {scale:0.0000}x the reference's, which is not a "
                + "framerate pair \u2014 a different cut, left for the alignment to report");
            return null;
        }

        try
        {
            var rescaled = Path.Combine(tempDir, "rescaled-input.srt");
            using (var writer = new StreamWriter(rescaled, false, new System.Text.UTF8Encoding(false)))
            {
                foreach (var line in File.ReadLines(targetPath))
                {
                    var arrow = line.IndexOf("-->", StringComparison.Ordinal);
                    if (arrow > 0
                        && TryParseSrtTime(line[..arrow].Trim(), out var startMs)
                        && TryParseSrtTime(line[(arrow + 3)..].Trim(), out var endMs))
                    {
                        writer.WriteLine(
                            $"{SrtWriter.FormatTime((long)Math.Round(startMs * scale))} --> "
                            + SrtWriter.FormatTime((long)Math.Round(endMs * scale)));
                    }
                    else
                    {
                        writer.WriteLine(line);
                    }
                }
            }

            PluginLog.Info(
                $"[{job.Id}] framerate: the subtitle's span is {scale:0.0000}x the reference's (a framerate pair) "
                + "\u2014 rescaling it onto the reference's time base before aligning, because a subtitle "
                + "reference cannot fix a framerate mismatch itself");
            return rescaled;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not rescale {Path} onto the reference's time base", targetPath);
            return null;
        }
    }

    /// <summary>
    /// Parses SRT cue start times (seconds).
    /// </summary>
    internal static List<double>? ParseSrtCueStarts(string path)
    {
        try
        {
            var starts = new List<double>();
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                var arrow = trimmed.IndexOf("-->", StringComparison.Ordinal);
                if (arrow <= 0)
                {
                    continue;
                }

                var ts = trimmed[..arrow].Trim();
                var parts = ts.Split(':', ',', '.');
                if (parts.Length < 4)
                {
                    continue;
                }

                if (int.TryParse(parts[0], out var h)
                    && int.TryParse(parts[1], out var m)
                    && int.TryParse(parts[2], out var s))
                {
                    // The fraction matters: SRT writes milliseconds (",500"), and dropping them
                    // truncated every cue to a whole second. A real 400 ms shift then measured as
                    // "0 ms offset" - which would have skipped saving a genuine correction. Some
                    // tools write one or two digits, so scale by the digit count.
                    var fraction = 0.0;
                    if (parts.Length > 3 && int.TryParse(parts[3], out var frac))
                    {
                        fraction = frac / Math.Pow(10, parts[3].Length);
                    }

                    starts.Add((h * 3600.0) + (m * 60.0) + s + fraction);
                }
            }

            return starts.Count >= 3 ? starts : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// What a successful sync actually changed, measured by comparing the cue timings of the input
    /// and the synced file.
    /// </summary>
    /// <param name="ShiftMs">Median displacement of the cues, in milliseconds (signed; + = moved later).</param>
    /// <param name="Ratio">Fitted time ratio; 1.0 when no framerate correction was needed.</param>
    /// <param name="DriftMs">Cumulative drift the ratio fixes over the subtitle's runtime.</param>
    /// <param name="SpreadMs">
    /// Interquartile range of the per-cue displacement, in milliseconds. A median alone cannot tell a ruler that
    /// matches from a ruler that is not this cut: measured on 2026-09-15, a subtitle aligned against the same
    /// track from a 2 % longer cut showed a median of -26,52 s - under the 30 s reference ceiling, so nothing
    /// refused it - with an IQR of 27,76 s, where the correct sibling scored an IQR of 0,00 s.
    /// </param>
    /// <param name="RangeMs">Largest minus smallest displacement, in milliseconds.</param>
    internal readonly record struct SyncChange(long ShiftMs, double Ratio, long DriftMs, long SpreadMs = 0, long RangeMs = 0)
    {
        /// <summary>
        /// Gets a value indicating whether the sync changed nothing: no offset and no framerate
        /// correction. ffsubsync still writes an output file in that case, and a synced sidecar whose
        /// timings are identical to its source is only noise in the library.
        /// </summary>
        public bool IsNoChange => ShiftMs == 0 && Math.Abs(Ratio - 1.0) < 1e-4;

        /// <summary>
        /// Describes the change for the log and the History list.
        /// </summary>
        /// <returns>A short human-readable description.</returns>
        public string Describe() => Math.Abs(Ratio - 1.0) < 1e-4
            ? $"{ShiftMs:+0;-0} ms offset"
            : $"{ShiftMs:+0;-0} ms offset at start \u00b7 framerate ratio {Ratio:0.0000}\u00d7 (\u2248{DriftMs:+0;-0} ms cumulative drift)";
    }

    /// <summary>
    /// How the engine's answer moves the cues: the pieces it moved them in, how flat each piece is, and the
    /// largest displacement any cue got.
    /// </summary>
    /// <param name="Segments">How many runs of consecutive cues share one displacement.</param>
    /// <param name="MaxWithinSpreadMs">The widest displacement span inside any one run, in milliseconds.</param>
    /// <param name="LargestShiftMs">The largest displacement any cue got, in absolute milliseconds.</param>
    /// <param name="SmallestStepMs">
    /// The smallest jump between two neighbouring pieces. This is what tells a staircase from a ramp: a piece
    /// boundary is a real step, while a rescale creeps by one engine sample at a time and produces a piece per
    /// cue.
    /// </param>
    internal readonly record struct SegmentStructure(
        int Segments, long MaxWithinSpreadMs, long LargestShiftMs, long SmallestStepMs);

    /// <summary>
    /// How different two neighbouring cues' displacements must be before they count as different pieces.
    /// </summary>
    /// <remarks>
    /// One engine sample: its speech signal is a 100 Hz series, so offsets are quantized to 10 ms and a
    /// difference below that is the same offset written twice. Not a policy value - a resolution.
    /// </remarks>
    internal const long EngineSampleMs = 10;

    /// <summary>
    /// Reads a sync as a piecewise-constant displacement rather than a line through it.
    /// </summary>
    /// <remarks>
    /// Why this exists (C2, measured 2026-09-15): with a split penalty the engine may move different parts of
    /// the subtitle by different amounts, and the plugin's own guard then sees a *linear* ratio away from 1.0
    /// and refuses a result that is right in both halves. Measured on the 48-minute episode with a 20 s
    /// discontinuity inserted at its midpoint: the piecewise answer is +7,54 s then +27,54 s-20 s (both halves
    /// within 60 ms of the truth), and the least-squares line through it reads 1,01082x - which
    /// <see cref="IsRescaleAcceptable"/> refuses, because 1,01082 is not a framerate pair. The line is the
    /// wrong reading of a step; this is the right one.
    /// </remarks>
    /// <param name="inputPath">The subtitle the engine was given.</param>
    /// <param name="outputPath">The subtitle the engine wrote.</param>
    /// <param name="stepToleranceMs">How far apart two neighbouring displacements must be to be a new piece.</param>
    /// <returns>The structure, or null when the two files cannot be compared cue for cue.</returns>
    internal static SegmentStructure? MeasureSegmentStructure(string inputPath, string outputPath, long stepToleranceMs)
    {
        var before = ParseSrtCueStarts(inputPath);
        var after = ParseSrtCueStarts(outputPath);
        if (before is null || after is null || before.Count < 3 || after.Count != before.Count)
        {
            return null;
        }

        var segments = 1;
        var largestShift = 0L;
        var runMinMs = (long)Math.Round((after[0] - before[0]) * 1000.0);
        var runMaxMs = runMinMs;
        var previousPieceMs = runMinMs;
        var maxWithin = 0L;
        long? smallestStepMs = null;

        for (var i = 1; i < before.Count; i++)
        {
            var shiftMs = (long)Math.Round((after[i] - before[i]) * 1000.0);
            largestShift = Math.Max(largestShift, Math.Abs(shiftMs));

            if (Math.Abs(shiftMs - runMaxMs) > stepToleranceMs || Math.Abs(shiftMs - runMinMs) > stepToleranceMs)
            {
                // A new piece: close the one that ran so far, and start this one. The jump between the two
                // pieces is recorded, because a ramp is a long series of jumps the size of one sample.
                maxWithin = Math.Max(maxWithin, runMaxMs - runMinMs);
                var stepMs = Math.Abs(shiftMs - previousPieceMs);
                smallestStepMs = smallestStepMs is null ? stepMs : Math.Min(smallestStepMs.Value, stepMs);
                previousPieceMs = shiftMs;
                segments++;
                runMinMs = shiftMs;
                runMaxMs = shiftMs;
                continue;
            }

            runMinMs = Math.Min(runMinMs, shiftMs);
            runMaxMs = Math.Max(runMaxMs, shiftMs);
        }

        maxWithin = Math.Max(maxWithin, runMaxMs - runMinMs);
        return new SegmentStructure(segments, maxWithin, largestShift, smallestStepMs ?? 0);
    }

    /// <summary>
    /// Decides whether a piecewise reading of the engine's answer is trustworthy enough to write.
    /// </summary>
    /// <remarks>
    /// The bar is the one the plugin already uses for "these cues moved together" - the spread ceiling that
    /// <see cref="RulerSpreadTooWide"/> refuses a subtitle ruler past (a quarter of the configured reference
    /// ceiling, so it carries no constant of its own) - plus the search window every displacement must stay
    /// inside, and every step between two pieces at least that large. A *rescale* cannot pass it: a ramp is a
    /// long series of jumps one engine sample (10 ms) wide, so either it reads as one piece whose spread is the
    /// whole drift, or as a thousand pieces whose steps are 10 ms - both are refused, by the same tolerance. A
    /// staircase the engine paid a split penalty for is what can pass: few pieces, each flat, each step real.
    /// <para>
    /// The residual risk, stated rather than hidden: a wrong answer that happens to be stepwise and flat would
    /// be accepted. The engine charges `split-penalty` seconds of overlap per step, so a staircase is expensive
    /// to fake, and the subtitle-ruler cross-check (S31) is unaffected - it reads the median.
    /// </para>
    /// </remarks>
    /// <param name="structure">The measured structure.</param>
    /// <param name="spreadCeilingMs">How far the displacement may vary inside one piece.</param>
    /// <param name="windowMs">The search window a displacement must stay inside.</param>
    /// <returns>True when the piecewise reading holds.</returns>
    internal static bool PiecewiseHolds(SegmentStructure structure, double spreadCeilingMs, double windowMs)
        => structure.Segments >= 2
           && structure.MaxWithinSpreadMs <= Math.Max(1.0, spreadCeilingMs)
           && structure.SmallestStepMs >= Math.Max(1.0, spreadCeilingMs)
           && structure.LargestShiftMs <= windowMs;

    /// <summary>
    /// Framerate pairs ffsubsync can legitimately be correcting: 25/23.976 (PAL film speedup),
    /// 25/24, 24/23.976, their inverses, and the half/double cases.
    /// </summary>
    internal static readonly double[] KnownFramerateRatios =
    {
        1.04271, 1.04167, 1.00100, 0.99900, 0.96000, 0.95904, 1.25, 0.8, 2.0, 0.5
    };

    /// <summary>
    /// Decides whether a measured sync is safe to write.
    /// </summary>
    /// <remarks>
    /// A rescale is not a correction that can be partly right: every cue after the first moves by a
    /// growing amount, so a wrong ratio ruins a whole file rather than leaving it slightly off. With
    /// framerate correction switched off, any ratio away from 1.0 means the engine rescaled timings
    /// anyway - the failure this guard exists for - so the result is refused. With it switched on, a
    /// ratio is expected but has to be a real framerate pair. The shift bound catches the rest: a
    /// single offset is what <c>--max-offset-seconds</c> asked for, so a shift beyond double that
    /// bound means the timings moved for some other reason.
    /// </remarks>
    /// <param name="ratio">Measured time ratio between input and synced output (1.0 = no rescale).</param>
    /// <param name="shiftMs">Measured offset in milliseconds.</param>
    /// <param name="maxOffsetSeconds">The configured offset bound handed to ffsubsync.</param>
    /// <param name="framerateCorrectionEnabled">Whether rescaling was requested.</param>
    /// <returns>True when the output may be written.</returns>
    internal static bool IsRescaleAcceptable(double ratio, long shiftMs, int maxOffsetSeconds, bool framerateCorrectionEnabled)
    {
        if (framerateCorrectionEnabled)
        {
            foreach (var known in KnownFramerateRatios)
            {
                if (Math.Abs(ratio - known) <= 0.003)
                {
                    return Math.Abs(shiftMs) <= Math.Max(maxOffsetSeconds, 60) * 1000L * 20;
                }
            }

            return Math.Abs(ratio - 1.0) <= 0.005;
        }

        if (Math.Abs(ratio - 1.0) > 0.005)
        {
            return false;
        }

        return Math.Abs(shiftMs) <= Math.Max(maxOffsetSeconds, 60) * 1000L * 2;
    }

    /// <summary>
    /// Measures what a sync changed, by comparing cue timings of the original and the synced file.
    /// </summary>
    /// <param name="inputPath">Subtitle handed to ffsubsync.</param>
    /// <param name="outputPath">Subtitle ffsubsync wrote.</param>
    /// <returns>The measurement, or null when the files cannot be compared (too few cues, non-SRT).</returns>
    internal static SyncChange? MeasureSyncChange(string inputPath, string outputPath)
    {
        var before = ParseSrtCueStarts(inputPath);
        var after = ParseSrtCueStarts(outputPath);
        if (before is null || after is null || before.Count < 3 || after.Count != before.Count)
        {
            return null;
        }

        var n = before.Count;
        var diffs = new List<double>(n);
        double sumX = 0, sumY = 0, sumXY = 0, sumXX = 0;
        for (var i = 0; i < n; i++)
        {
            var x = before[i];
            var y = after[i];
            diffs.Add(y - x);
            sumX += x;
            sumY += y;
            sumXY += x * y;
            sumXX += x * x;
        }

        diffs.Sort();
        var shiftMs = (long)Math.Round(diffs[n / 2] * 1000.0);

        var denom = (n * sumXX) - (sumX * sumX);
        double ratio = 1.0;
        if (Math.Abs(denom) > 1e-9)
        {
            ratio = ((n * sumXY) - (sumX * sumY)) / denom;
        }

        var driftMs = (long)Math.Round((ratio - 1.0) * before[^1] * 1000.0);

        // The spread of the displacement, not just its middle: cues that all moved by the same amount are a
        // sync (or a cut that matches), cues that moved by wildly different amounts are a ruler that does not
        // belong to this film wherever the median happens to land.
        var spreadMs = (long)Math.Round((diffs[(3 * n) / 4] - diffs[n / 4]) * 1000.0);
        var rangeMs = (long)Math.Round((diffs[^1] - diffs[0]) * 1000.0);
        return new SyncChange(shiftMs, ratio, driftMs, spreadMs, rangeMs);
    }

    /// <summary>
    /// Whether a subtitle looks like a forced/signs track rather than the full one: very few cues for
    /// a long video.
    ///
    /// A full episode subtitle carries hundreds of cues (roughly one every few seconds), so a handful
    /// over more than ten minutes is a track that only translates on-screen text. Nothing else about
    /// the output shows it - the synced sidecar is perfectly valid, it just contains two lines - which
    /// is why this is stated in the log and in the job's outcome.
    /// </summary>
    /// <param name="cueCount">Number of cues in the subtitle that was synced.</param>
    /// <param name="duration">Runtime of the video.</param>
    /// <returns>True when the track is suspiciously sparse.</returns>
    public static bool LooksLikeSignsTrack(int cueCount, TimeSpan duration)
        => cueCount > 0 && cueCount < 12 && duration > TimeSpan.FromMinutes(10);

    /// <summary>
    /// Counts the cues in a subtitle file. Returns -1 when it cannot be read, which is treated as
    /// "unknown" rather than as zero cues.
    /// </summary>
    /// <param name="path">Subtitle path.</param>
    /// <returns>Cue count, or -1.</returns>
    internal static int CountSubtitleCues(string path)
    {
        try
        {
            return SrtWriter.CountCues(File.ReadAllText(path));
        }
        catch (IOException)
        {
            return -1;
        }
        catch (UnauthorizedAccessException)
        {
            return -1;
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

    internal static string? DescribeSyncChange(string inputPath, string outputPath)
        => MeasureSyncChange(inputPath, outputPath)?.Describe();

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
        string? referenceStream = null;

        // True when this job is aligned against a subtitle taken from a sibling track instead of
        // against the audio. Such a result is only ever as good as that track, so it is checked
        // before anything is written.
        var usedSubtitleReference = false;
        var stretchDropped = false;
        var audioFallback = false;
        var wideAllowanceApplied = false;

        // Which track became the reference, for both log lines and the outcome text.
        string? referenceSpec = null;

        // Duration of the video, used to judge whether a track is a full subtitle or just signs.
        var videoDurationForReference = video.RunTimeTicks is { } refTicks && refTicks > 0
            ? TimeSpan.FromTicks(refTicks)
            : TimeSpan.Zero;

        // Paths for the safe atomic-replace workflow
        string? backupPath = null;   // .bak of original subtitle file (replace mode only)
        string? tempOutput = null;   // ffsubsync output in temp dir
        string? changedDir = null;   // folder touched by this job (for the targeted library rescan)
        string? cuesNote = null;     // set when the subtitle looks like a signs/forced track

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
                    referenceStream = SelectReferenceStream(
                        false,
                        siblings.Select(stream => stream.Codec ?? string.Empty).ToList(),
                        -1,
                        siblings.Select(stream => stream.IsForced).ToList());
                    _logger.LogInformation(
                        "External sync of {Subtitle}: aligning against '{Reference}' of {Video} instead of the audio",
                        subtitleInputPath,
                        referenceStream,
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
                var (containerIndex, subtitleStreamOrdinal, subtitleCodecs) = await ResolveContainerSubtitleIndexAsync(video, subtitleStream).ConfigureAwait(false);

                // Keep ffsubsync from using the very track we are fixing as its speech
                // signal (see SelectReferenceStream) — that would report every embedded
                // subtitle as already in sync.
                var embeddedSubtitleStreams = video.GetMediaSources(true)
                    .SelectMany(source => source.MediaStreams)
                    .Where(stream => stream.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !stream.IsExternal)
                    .ToList();

                referenceStream = SelectReferenceStream(
                    true,
                    subtitleCodecs,
                    subtitleStreamOrdinal,
                    embeddedSubtitleStreams.Count == subtitleCodecs.Count
                        ? embeddedSubtitleStreams.Select(stream => stream.IsForced).ToList()
                        : null);
                _logger.LogInformation(
                    "Embedded sync of {Video}: deriving the speech signal from '{Reference}'",
                    videoPath, referenceStream);

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
            var inputCues = CountSubtitleCues(subtitleInputPath);
            var videoDuration = video.RunTimeTicks is { } ticks && ticks > 0
                ? TimeSpan.FromTicks(ticks)
                : TimeSpan.Zero;
            if (LooksLikeSignsTrack(inputCues, videoDuration))
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
            var mode = NormalizeMode(job.Mode);
            var referencePath = videoPath;
            var serializeSpeech = false;
            string? speechKey = null;
            var usingCachedSpeech = false;

            // The reference this job is aligned against is one of exactly two things: the file's own
            // audio (analysed once, reused through the speech cache) or a sibling subtitle track that
            // our own reader produced. What it must never be is the media file itself: passing the
            // container to ffsubsync makes the engine demux the whole thing with its own ffmpeg, once
            // per job. Measured on this fixture — a 2.38 GB episode — two jobs sat in that demux for 7
            // and 17 minutes and never finished, and on a bulk run that is what stops the batch ever
            // reaching the end (S11).
            var usesAudioReference = referenceStream is null
                || referenceStream.StartsWith("a:", StringComparison.Ordinal);

            // The speech signal depends on the media file, the VAD method and the engine
            // build — never on the subtitle, its language or the mode. Analysing it is work
            // that happens anyway, so it is always kept: the other subtitles of that file and
            // any later run then skip the audio pass entirely. This is also the fallback whenever a
            // subtitle reference cannot be built — the plugin never refuses a job, it changes the
            // ruler it measures against.
            async Task<string> PrepareAudioReferenceAsync(string why)
            {
                // Identity of everything that shapes the speech signal: the binary in use,
                // the bundled engine version and this plugin's own version. A path alone is
                // not enough — an upgraded bundled binary keeps its path.
                var engineIdentity = string.Join(
                    "|",
                    ResolveFfSubSyncPath(),
                    BundledFfSubSyncVersion,
                    typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
                // The analysis this key names is the one the engine will produce, so the key names the VAD the
                // engine is actually given for the audio path - not the configured one, which it is not given.
                speechKey = SpeechCache.KeyFor(
                    videoPath,
                    AudioReferenceVad + "|audio",
                    engineIdentity);
                var cached = SpeechCache.TryGet(speechKey);
                if (cached is not null)
                {
                    usingCachedSpeech = true;
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

                var harvestedWhileWaiting = SpeechCache.TryGet(speechKey);
                if (harvestedWhileWaiting is not null)
                {
                    speechGate.Release();
                    job.HoldsSpeechGate = false;
                    usingCachedSpeech = true;
                    job.Phase = SyncPhaseLabel(fromCache: true, audioReference: true);
                    _logger.LogInformation("Reusing the audio analysis another job stored for {Video} ({Why})", videoPath, why);
                    PluginLog.Info($"[{job.Id}] reference: method=speech-cache why={why} (harvested by another job of this file while this one waited)");
                    return harvestedWhileWaiting;
                }

                serializeSpeech = true;
                job.Phase = SyncPhaseLabel(fromCache: false, audioReference: true);
                PluginLog.Info($"[{job.Id}] reference: method=audio why={why} (this job does the file's analysis; the others wait for it)");
                return SpeechCache.CreateReferenceLink(videoPath, speechKey);
            }

            if (usesAudioReference)
            {
                referencePath = await PrepareAudioReferenceAsync("the audio is this job's own reference")
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
                    BundledFfSubSyncVersion,
                    typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");
                var referenceOrdinal = SubtitleStreamOrdinal(referenceStream);
                referenceSpec = referenceStream;

                // The reference lives in this run's own directory and is shared with the other
                // subtitles of this file while they are still going to use it. It is deliberately
                // never carried over from an earlier run: a reference taken from a sibling subtitle
                // inherits that track's own error, and every other track of the file then inherits it
                // in turn.
                var referenceTarget = referenceOrdinal >= 0
                    ? ReferenceStore.Reserve(videoPath, referenceStream!, referenceIdentity)
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
                            if (LooksLikeSignsTrack(reuseCues, videoDurationForReference))
                            {
                                // A reference with a handful of cues over a whole episode is a
                                // signs/forced track: it cannot align anything. Take it out of the
                                // running and let this job use the audio instead - the sync still
                                // happens, it just stops being built on a bad ruler.
                                _logger.LogWarning(
                                    "The reference subtitle {Track} for {Video} holds only {Cues} cue(s) - a signs track, not usable as a reference; syncing against the audio instead",
                                    referenceSpec,
                                    videoPath,
                                    reuseCues);
                                PluginLog.Info(
                                    $"[{job.Id}] reference {referenceSpec} has only {reuseCues} cue(s) (a signs/forced track), "
                                    + "so it is not usable as a ruler - falling back to the audio for this job");
                                ReferenceStore.Discard(videoPath, referenceSpec!);
                                referenceTarget = null;
                                referenceWhy = $"only {reuseCues} cue(s): a signs/forced track";
                            }
                            else
                            {
                                referencePath = referenceTarget;
                                referenceStream = null;
                                usedSubtitleReference = true;
                                job.Phase = SyncPhaseLabel(fromCache: true, audioReference: false);
                                _logger.LogInformation(
                                    "Reusing this run's reference subtitle for {Video}: track {Reference}, {Cues} cues",
                                    videoPath,
                                    referenceSpec,
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
                                    referencePath = referenceTarget;
                                    referenceStream = null;
                                    usedSubtitleReference = true;
                                    job.Phase = SyncPhaseLabel(fromCache: false, audioReference: false);
                                    _logger.LogInformation(
                                        "Built this run's reference subtitle for {Video} from track {Reference}: {Cues} cues (deleted once this file's subtitles are done)",
                                        videoPath,
                                        referenceSpec,
                                        SrtWriter.CountCues(referenceText));
                                    PluginLog.Info(
                                        $"[{job.Id}] reference: method=subtitle cues={SrtWriter.CountCues(referenceText)} "
                                        + $"track={referenceSpec} file={videoPath}");
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

                if (!usedSubtitleReference)
                {
                    // No usable subtitle reference for this job. The audio is analysed instead —
                    // through the speech cache, so that a file's other subtitles pay for it once —
                    // and the container is never handed over: that demux is what left a bulk run
                    // unable to finish.
                    PluginLog.Info(
                        $"[{job.Id}] reference {referenceSpec ?? "(none)"} unusable ({referenceWhy ?? "not available"}) "
                        + "- aligning against the audio instead");
                    _logger.LogWarning(
                        "No usable reference subtitle for {Video} ({Why}); syncing against the audio instead",
                        videoPath,
                        referenceWhy ?? "not available");
                    referenceSpec = null;
                    referenceStream = null;
                    referencePath = await PrepareAudioReferenceAsync(
                        "no reference subtitle could be built for this job").ConfigureAwait(false);
                }
            }

            // The engine is given a rescaled copy when a frame rate difference stands between the two subtitles;
            // the measurement below still compares the original subtitle with the output, so the rescale is
            // visible to the guard rather than hidden from it.
            var engineInput = subtitleInputPath;
            var referenceArg = referencePath!;
            if (usedSubtitleReference && config.FixFramerate)
            {
                engineInput = RescaleOntoReferenceSpan(subtitleInputPath, referenceArg, videoDuration, tempDir, job) ?? engineInput;
            }

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
            var exitCode = await RunProcessWithStderrCallbackAsync(
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

                exitCode = await RunProcessWithStderrCallbackAsync(
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
                // Harvest covers the fallback where ffsubsync wrote the .npz next to the
                // media file; DropLink removes the temporary symlink either way.
                SpeechCache.Harvest(referencePath, speechKey);
                SpeechCache.DropLink(speechKey);
                SpeechCache.Prune();
            }

            ReleaseSpeechGate(job, videoPath);

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
                job.Outcome = "already in sync (shift under 3 s) \u2014 no change needed"
                    + (cuesNote is null ? string.Empty : " \u00b7 " + cuesNote);
                job.Phase = "Complete";
                job.Status = SyncJobStatus.Completed;
                job.Progress = 1.0;
                _logger.LogInformation("Sync job {JobId}: subtitle already in sync \u2014 no output written", job.Id);
                LogPluginCompletion(job, null);
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
            var measured = MeasureSyncChange(engineInput, tempOutput);

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
            var spreadCeilingMs = referenceCeilingMs * SubtitleReferenceSpreadFraction;
            var rulerSpreadTooWide = measured is { } spread && RulerSpreadTooWide(spread, referenceCeilingMs);
            if (usedSubtitleReference
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
                    ? $"aligned to the reference subtitle {referenceSpec} at {fromReference.ShiftMs} ms"
                    : $"the cues did not move together against {referenceSpec}: a spread of "
                        + $"{fromReference.SpreadMs / 1000.0:0.00} s across the middle half of its cues "
                        + $"(range {fromReference.RangeMs / 1000.0:0.00} s) while the median was "
                        + $"{fromReference.ShiftMs} ms";
                _logger.LogWarning(
                    "Sync job {JobId}: the reference subtitle is not the same cut ({Detail}) - aligning against the audio instead",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id}: the reference subtitle {referenceSpec} is not the same cut ({detail}, over the "
                    + $"{referenceCeilingMs / 1000.0:0.#} s limit for a subtitle reference, itself under the "
                    + $"{spreadCeilingMs / 1000.0:0.#} s spread limit) \u2014 discarding that track as a ruler and "
                    + $"aligning against the audio instead, file={video.Path}");

                if (referenceSpec is not null)
                {
                    ReferenceStore.Discard(videoPath, referenceSpec);
                }

                var audioReference = await PrepareAudioReferenceAsync(
                    "the reference subtitle is not the same cut as the video").ConfigureAwait(false);
                var audioArgs = BuildFfSubSyncArgs(
                    config, audioReference, subtitleInputPath, tempOutput, tempDir, serializeSpeech, null,
                    vadOverride: AudioReferenceVad);

                double? audioScore = null;
                double? audioOffsetSeconds = null;
                var audioExit = await RunProcessWithStderrCallbackAsync(
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

                if (audioExit == 0 && File.Exists(tempOutput))
                {
                    if (speechKey is not null && serializeSpeech)
                    {
                        SpeechCache.Harvest(audioReference, speechKey);
                        SpeechCache.DropLink(speechKey);
                        SpeechCache.Prune();
                    }

                    ReleaseSpeechGate(job, videoPath);
                    usedSubtitleReference = false;
                    referenceSpec = null;
                    referenceStream = null;
                    referenceArg = audioReference;
                    audioFallback = true;
                    measured = MeasureSyncChange(subtitleInputPath, tempOutput);
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
                    job.Status = SyncJobStatus.Failed;
                    job.Phase = "Refused";
                    job.Error = $"refused: the subtitle was aligned against the file's own subtitle track {referenceSpec}, "
                        + $"which demanded a {fromReference.ShiftMs} ms shift — that track is not the same cut — and "
                        + "aligning against the audio instead produced nothing. Nothing was written.";
                    job.Progress = 1.0;
                    job.FinishedAtUtc = DateTime.UtcNow;
                    job.OutputPath = null;
                    SafeDelete(tempOutput);
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
                    config, referenceArg, engineInput, wideOutput, tempDir, serializeSpeech, referenceStream,
                    VadForReference(referenceSpec));
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
                var wideExit = await RunProcessWithStderrCallbackAsync(
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
                    new EngineWatch(job.Id, Path.GetFileName(videoPath), referenceStream ?? "(default)")).ConfigureAwait(false);

                var wideChange = wideExit == 0 && File.Exists(wideOutput)
                    ? MeasureSyncChange(engineInput, wideOutput)
                    : null;

                if (wideChange is { } wider && Math.Abs(wider.ShiftMs) < wideLimitMs - 500)
                {
                    // The wider window produced an answer inside itself: the engine is not clamped any more.
                    var verifyOutput = Path.Combine(tempDir, "wide-check.srt");
                    SafeDelete(verifyOutput);
                    var verifyArgs = BuildFfSubSyncArgs(
                        config, referenceArg, wideOutput, verifyOutput, tempDir, serializeSpeech, referenceStream,
                        VadForReference(referenceSpec));
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

                    var verifyExit = await RunProcessWithStderrCallbackAsync(
                        ffsubsyncExe, verifyArgs, tempDir, null, cancellationToken,
                        new EngineWatch(job.Id, Path.GetFileName(videoPath), referenceStream ?? "(default)")).ConfigureAwait(false);
                    var residual = verifyExit == 0 && File.Exists(verifyOutput)
                        ? MeasureSyncChange(wideOutput, verifyOutput)
                        : null;
                    var residualRatio = residual?.Ratio ?? 1.0;
                    var residualShift = residual?.ShiftMs ?? 0;

                    if (verifyExit == 0
                        && AlignmentHoldsAgainstAudio(residualRatio, residualShift, videoDuration.TotalSeconds))
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
                        job.Status = SyncJobStatus.Failed;
                        job.Phase = "Refused";
                        job.Error = $"refused: this subtitle is further out than the {Configuration.SettingsValidation.MaxOffsetSecondsOf(config)} s search "
                            + $"window ({onCeiling.ShiftMs} ms reached it), the {wideSeconds} s window measured "
                            + $"{wider.ShiftMs} ms, and that did not hold up against the film's audio: {why}. Nothing "
                            + "was written.";
                        job.Progress = 1.0;
                        job.FinishedAtUtc = DateTime.UtcNow;
                        job.OutputPath = null;
                        SafeDelete(tempOutput);
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
                    _logger.LogWarning(
                        "Sync job {JobId}: refusing after the wider window ({Detail}) — nothing written",
                        job.Id,
                        detail);
                    PluginLog.Info(
                        $"job {job.Id} REFUSED: this subtitle is further out than the plugin is searching "
                        + $"({detail}); raise \"Maximum offset\" and run it again, or sync it by hand. Nothing "
                        + $"written, source untouched, file={video.Path}");
                    job.Status = SyncJobStatus.Failed;
                    job.Phase = "Refused";
                    job.Error = $"refused: {detail}. Raise \"Maximum offset\" in the plugin settings (it is the search "
                        + "window the alignment may look in) and run it again. Nothing was written.";
                    job.Progress = 1.0;
                    job.FinishedAtUtc = DateTime.UtcNow;
                    job.OutputPath = null;
                    SafeDelete(tempOutput);
                    return;
                }
            }

            // Nothing destructive is ever written: a measured rescale that was not asked for (or that
            // is not a real framerate pair) means the engine moved the timeline, and the source
            // subtitle stays untouched while the job says exactly why. A *piecewise* answer is the one
            // exception, and only when the plugin asked for one (C2): see PiecewiseHolds.
            var piecewiseRequested = Configuration.SettingsValidation.SplitPenaltyOf(config) > 0;
            var structure = piecewiseRequested && measured is not null
                ? MeasureSegmentStructure(engineInput, tempOutput, EngineSampleMs)
                : null;
            var piecewiseHolds = structure is { } pieces
                && PiecewiseHolds(pieces, spreadCeilingMs, Configuration.SettingsValidation.MaxOffsetSecondsOf(config) * 1000.0);
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
                && !IsRescaleAcceptable(scaled.Ratio, scaled.ShiftMs, Configuration.SettingsValidation.MaxOffsetSecondsOf(config), config.FixFramerate))
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
                job.Status = SyncJobStatus.Failed;
                job.Phase = "Refused";
                job.Error = $"refused: the engine rescaled the timings ({detail}) and nothing was written. "
                    + (config.FixFramerate
                        ? "This is not a framerate pair a release could really have."
                        : "Turn on \"Correct framerate mismatch\" only for subtitles from a different framerate.");
                job.Progress = 1.0;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.OutputPath = null;
                SafeDelete(tempOutput);
                return;
            }

            var suspiciousMs = referenceCeilingMs * SuspiciousReferenceShiftFraction;
            var agreementMs = referenceCeilingMs * SubtitleReferenceAudioAgreementFraction;
            if (usedSubtitleReference
                && measured is { } fromReferenceNote
                && Math.Abs(fromReferenceNote.ShiftMs) > suspiciousMs)
            {
                // The plugin's own words for a shift this size are "usually means that track is not the same cut",
                // and until now it wrote the result anyway with that note on it. Instead, ask the one ruler that
                // cannot be a different cut - the film's own audio - and let the two answers decide. It costs one
                // audio analysis, cached per file like every other audio path, and only for shifts in this band.
                cuesNote = Join(cuesNote, $"aligned to the reference subtitle {referenceSpec} at {fromReferenceNote.ShiftMs} ms - "
                    + "worth checking, a shift this size usually means the reference track is not the same cut");
                _logger.LogInformation(
                    "Sync job {JobId}: aligned to the reference subtitle {Reference} at {Shift} ms",
                    job.Id,
                    referenceSpec,
                    fromReferenceNote.ShiftMs);
                PluginLog.Info(
                    $"[{job.Id}] note: aligned to the reference subtitle {referenceSpec} at {fromReferenceNote.ShiftMs} ms "
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
                    "the reference subtitle asked for a shift worth checking").ConfigureAwait(false);
                // `webrtc`, not the configured VAD: with the default `subs_then_webrtc` the engine takes the
                // video's *embedded subtitles* as the speech signal, and the ruler being cross-checked is one of
                // them - measured 2026-09-15, the "audio" run then returned the ruler's own answer (24 170 ms
                // against the film's real -0,08 s), i.e. it confirmed the very track it was meant to check.
                var crossArgs = BuildFfSubSyncArgs(
                    config, crossReference, subtitleInputPath, crossCheckOutput, tempDir, serializeSpeech, null,
                    vadOverride: AudioReferenceVad);
                double? crossScore = null;
                double? crossOffset = null;
                PluginLog.Info(
                    $"[{job.Id}] cross-check run: reference={crossReference} input={subtitleInputPath} "
                    + $"args={string.Join(' ', crossArgs)}");
                var crossExit = await RunProcessWithStderrCallbackAsync(
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
                    ? MeasureSyncChange(subtitleInputPath, crossCheckOutput)
                    : null;
                if (fromAudio is { } audioChange)
                {
                    if (speechKey is not null && serializeSpeech)
                    {
                        SpeechCache.Harvest(crossReference, speechKey);
                        SpeechCache.DropLink(speechKey);
                        SpeechCache.Prune();
                    }

                    ReleaseSpeechGate(job, videoPath);
                    var disagreementMs = Math.Abs(audioChange.ShiftMs - fromReferenceNote.ShiftMs);
                    if (RulersDisagree(fromReferenceNote.ShiftMs, audioChange.ShiftMs, referenceCeilingMs))
                    {
                        // The two rulers disagree about this film, and only one of them can be a different cut.
                        File.Copy(crossCheckOutput, tempOutput, overwrite: true);
                        if (referenceSpec is not null)
                        {
                            ReferenceStore.Discard(videoPath, referenceSpec);
                        }

                        var wasSpec = referenceSpec;
                        usedSubtitleReference = false;
                        referenceSpec = null;
                        referenceStream = null;
                        referenceArg = crossReference;
                        audioFallback = true;
                        measured = MeasureSyncChange(subtitleInputPath, tempOutput);
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
                : MeasureSyncChange(subtitleInputPath, tempOutput);

            if (changedForUser is { IsNoChange: true } noChange)
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
                return;
            }

            // S8, settled with the user on 2026-09-11: a result whose only ruler was the audio cannot be
            // checked against anything. Measured on this project's own fixture, a subtitle that was
            // already in sync came back "+1780 ms offset" from the audio alone and was written as a
            // plain success. For a track *inside* the file that is a guess, so nothing is written and
            // the job says exactly that. An external sidecar has no other ruler to fall back on — the
            // audio IS its reference — so that path still writes, as it always has.
            if (!usedSubtitleReference && !subtitleStream.IsExternal)
            {
                var detail = measured is { } audioOnly ? audioOnly.Describe() : "no measurable change";
                _logger.LogWarning(
                    "Sync job {JobId}: refusing to write an audio-only alignment ({Detail}) — nothing written",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id} UNVERIFIED: the audio was the only ruler ({detail}) and this track has no "
                    + $"reference subtitle to check it against; nothing written, source untouched, file={video.Path}");
                job.Status = SyncJobStatus.Failed;
                job.Phase = "Unverified \u2014 audio-only alignment";
                job.Error = $"unverified: this subtitle was aligned against the audio ({detail}) and the file holds "
                    + "no other text track to check that against, so nothing was written. An audio reference on a "
                    + "short file can be out by a second or more. Sync it against a subtitle track if the file has "
                    + "one, or run it from an external .srt, where the audio is the reference the plugin is meant "
                    + "to use.";
                job.Progress = 1.0;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.OutputPath = null;
                SafeDelete(tempOutput);
                return;
            }

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
                    var target = lang is not null && string.Equals(stem, lang, StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(dir, $"{lang}.SYNCED.srt")
                        : Path.Combine(dir, stem + ".SYNCED.srt");

                    RequireWritable(dir);
                    File.Copy(tempOutput, target, overwrite: true);
                    job.OutputPath = target;
                    changedDir = dir;
                    _logger.LogInformation("Synced copy written: {Original} → {Target} (stem={Stem}, lang={Lang}, original untouched)", original, target, stem, lang ?? "(none)");
                }
                else
                {
                    job.Phase = "Replacing subtitle";
                    job.Progress = 0.85;

                    RequireWritable(Path.GetDirectoryName(subtitleStream.Path) ?? ".");

                    // The backup path is chosen (and therefore known to the rollback below) *before*
                    // the original is touched: the destructive copy is inside ReplaceExternalSubtitle,
                    // and a failure there used to leave `backupPath` null, so the only rollback there is
                    // was skipped for exactly the case that needs it.
                    backupPath = NextBackupPath(subtitleStream.Path);
                    await ReplaceExternalSubtitle(subtitleStream.Path, backupPath, tempOutput).ConfigureAwait(false);
                    changedDir = Path.GetDirectoryName(subtitleStream.Path) ?? ".";

                    job.OutputPath = subtitleStream.Path;
                    _logger.LogInformation("Replaced external subtitle: {Path} (original kept at {Backup})", subtitleStream.Path, backupPath);
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

                RequireWritable(videoDir);
                File.Copy(tempOutput, target, overwrite: true);
                job.OutputPath = target;
                changedDir = videoDir;
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

            // Step 5: Success — describe what changed (offset ms / framerate). The replace-mode
            // backup is deliberately NOT deleted: a sync that succeeds while being wrong used to
            // leave the user with no way back, and the copy costs a few kilobytes. It is named
            // *.bak.subsync, which is not a subtitle extension, so Jellyfin never shows it as a
            // second track, and the result says where it is.
            var outcomeInput = backupPath ?? (subtitleStream.IsExternal ? subtitleStream.Path : subtitleInputPath);
            if (job.OutputPath is not null)
            {
                job.Outcome = DescribeSyncChange(outcomeInput, job.OutputPath);
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
                    var before = ParseSrtCueStarts(subtitleInputPath);
                    var after = ParseSrtCueStarts(engineInput);
                    var factor = before is { Count: > 2 } && after is { Count: > 2 }
                        ? (after[^1] - after[0]) / (before[^1] - before[0])
                        : 1.0;
                    var aligned = DescribeSyncChange(engineInput, job.OutputPath);
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
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Sync job {JobId} was killed by the user", job.Id);
            job.Status = SyncJobStatus.Cancelled;
            job.Phase = "Killed";
            job.Error = "Killed by the user.";
            job.FinishedAtUtc = DateTime.UtcNow;
        }
        catch (Exception ex)
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
        finally
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

            try
            {
                SharedExtractionStore.Release(video.Path, job.Id);
            }
            catch
            {
                // Non-critical: the next cleanup clears it.
            }
        }
    }

    /// <summary>
    /// The outcome of a bulk enqueue (D9): what was created, and what the queue already held.
    /// </summary>
    public sealed class BatchCreation
    {
        /// <summary>Gets the batch identifier, set whether or not any task was new.</summary>
        public string BatchId { get; init; } = string.Empty;

        /// <summary>Gets the jobs this call created (validation failures appear as failed rows).</summary>
        public IReadOnlyList<SyncJob> Jobs { get; init; } = Array.Empty<SyncJob>();

        /// <summary>
        /// Gets the jobs the queue already held for the requested item and track, which were therefore not
        /// queued a second time.
        /// </summary>
        public IReadOnlyList<SyncJob> AlreadyQueued { get; init; } = Array.Empty<SyncJob>();
    }

    /// <summary>
    /// Finds the queued or running job for the same item and subtitle track, if there is one (D9).
    /// </summary>
    /// <remarks>
    /// A second job for the same track is not a second opinion: it reads the same subtitle, runs the same
    /// alignment and writes the same output, while the first job's file gate makes it wait. Only live jobs
    /// count - a finished job is history, and syncing the same track again later is a legitimate request.
    /// </remarks>
    /// <param name="jobs">Jobs in queue order.</param>
    /// <param name="itemId">Media item.</param>
    /// <param name="subtitleIndex">Subtitle stream index.</param>
    /// <returns>The job already queued for that track, or null.</returns>
    internal static SyncJob? FindDuplicate(IEnumerable<SyncJob> jobs, Guid itemId, int subtitleIndex)
        => jobs.FirstOrDefault(job => job.ItemId == itemId
            && job.SubtitleIndex == subtitleIndex
            && job.Status is SyncJobStatus.Queued or SyncJobStatus.Running);

    /// <summary>How long teardown waits for the lanes and the pump to leave (B14).</summary>
    internal const int ShutdownWaitMs = 5000;

    /// <summary>
    /// Kills every child process this service started and reports how many really exited.
    /// </summary>
    /// <remarks>
    /// Cancelling a token only stops what polls it; ffsubsync spawns ffmpeg, and both hold the media file, so the
    /// trees are killed directly. Used by <see cref="KillAll"/> and by <c>Dispose</c> (B14).
    /// </remarks>
    /// <returns>How many trees were asked to stop, and how many had exited by the deadline.</returns>
    internal (int Asked, int Stopped) KillChildProcesses()
    {
        var asked = 0;
        var toKill = new List<Process>();
        foreach (var process in _liveProcesses.Values.ToList())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    asked++;
                    toKill.Add(process);
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not kill process {Pid}", process.Id);
            }
        }

        return (asked, CountExited(toKill, KillWaitMs));
    }

    /// <summary>
    /// Tracks a child process so a kill or a teardown can find it (B14).
    /// </summary>
    /// <param name="process">The process just started.</param>
    internal void TrackChildProcess(Process process)
    {
        _liveProcesses[process.Id] = process;
    }

    /// <summary>
    /// Waits, for a bounded time, for the extraction lanes and the pump to finish (B14).
    /// </summary>
    /// <param name="milliseconds">Deadline in milliseconds.</param>
    /// <returns>How many of those tasks had completed by the deadline.</returns>
    internal int WaitForLanes(int milliseconds)
        => WaitForTasks(
            _pumpTask is null ? _laneTasks : _laneTasks.Concat(new[] { _pumpTask }),
            milliseconds);

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

    /// <summary>
    /// True when an extraction attempt that produced no subtitle was stopped rather than failing (B20).
    /// </summary>
    /// <remarks>
    /// A killed pass and a pass that genuinely could not read the file both come back as "no text", and the reader
    /// reports the first as the reason <c>cancelled</c>. Treating the two the same sends a cancelled attempt down
    /// every remaining engine - more minutes of engine work on a file the user just asked it to stop reading - and
    /// ends with the job marked failed, which names an action nobody took.
    /// </remarks>
    /// <param name="reason">The reason the attempt reported.</param>
    /// <param name="token">The job's cancellation token.</param>
    /// <returns>True when this attempt was cancelled.</returns>
    internal static bool IsCancelledExtraction(string reason, CancellationToken token)
        => token.IsCancellationRequested
            || string.Equals(reason, "cancelled", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Stops the extraction chain when the attempt was cancelled, instead of trying the next engine (B20).
    /// </summary>
    /// <param name="reason">The reason the attempt reported.</param>
    /// <param name="token">The job's cancellation token.</param>
    /// <param name="where">Which attempt this was, for the log.</param>
    /// <exception cref="OperationCanceledException">Thrown when the attempt was cancelled.</exception>
    private void StopChainIfCancelled(string reason, CancellationToken token, string where)
    {
        if (!IsCancelledExtraction(reason, token))
        {
            return;
        }

        PluginLog.Info($"extract: {where} was stopped (killed by the user) — no fallback attempted");
        throw new OperationCanceledException(token);
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
    /// Picks the finished jobs a cleanup pass removes: everything past <paramref name="maxAge"/>, plus the
    /// oldest beyond <paramref name="maxJobs"/> when a run really has produced that many (B17).
    /// </summary>
    /// <param name="jobs">Every tracked job.</param>
    /// <param name="now">The current time.</param>
    /// <param name="maxAge">How long a finished job is kept.</param>
    /// <param name="maxJobs">Most finished jobs to keep at all.</param>
    /// <param name="isHistoryOnly">Answers whether a row is a restored history entry rather than a job.</param>
    /// <returns>Keys to remove.</returns>
    internal static List<string> JobsToEvict(
        IReadOnlyDictionary<string, SyncJob> jobs,
        DateTime now,
        TimeSpan maxAge,
        int maxJobs,
        Func<string, bool> isHistoryOnly)
    {
        var finished = jobs
            .Where(pair => !isHistoryOnly(pair.Key))
            .Where(pair => pair.Value.Status is SyncJobStatus.Completed or SyncJobStatus.Failed or SyncJobStatus.Cancelled)
            .OrderBy(pair => pair.Value.FinishedAtUtc ?? pair.Value.CreatedAtUtc)
            .ToList();

        var cutoff = now - maxAge;
        var toRemove = finished
            .Where(pair => (pair.Value.FinishedAtUtc ?? pair.Value.CreatedAtUtc) < cutoff)
            .Select(pair => pair.Key)
            .ToList();

        var kept = finished.Count - toRemove.Count;
        if (kept > maxJobs)
        {
            var over = kept - maxJobs;
            toRemove.AddRange(finished
                .Where(pair => !toRemove.Contains(pair.Key))
                .Take(over)
                .Select(pair => pair.Key));
        }

        return toRemove;
    }

    /// <summary>How long a killed process is given to actually exit before it is reported as a survivor.</summary>
    private const int KillWaitMs = 3000;

    /// <summary>
    /// Waits for processes to exit and reports how many really did (B16).
    /// </summary>
    /// <remarks>
    /// `WaitForExit` on each handle, not a sleep-and-sample loop: the caller wants a count that was measured,
    /// and a process that ignored SIGKILL for longer than the deadline has to be reported as still alive
    /// rather than folded into a success figure.
    /// </remarks>
    /// <param name="processes">Processes that were asked to stop.</param>
    /// <param name="waitMs">How long each one may take.</param>
    /// <returns>How many of them exited.</returns>
    internal static int CountExited(IEnumerable<Process> processes, int waitMs)
    {
        var exited = 0;
        foreach (var process in processes)
        {
            try
            {
                if (process.WaitForExit(waitMs) && process.HasExited)
                {
                    exited++;
                }
            }
            catch (Exception)
            {
                // A process that cannot be waited on is not counted as stopped.
            }
        }

        return exited;
    }

    /// <summary>
    /// Asks whether a directory name is one this plugin creates for a job's scratch space (B23).
    /// </summary>
    /// <remarks>
    /// Job ids are <c>Guid.NewGuid().ToString("N")</c> - 32 lowercase hex characters and nothing else - so the
    /// test is exact rather than a prefix or a "looks like an id" match. That is what keeps a recursive delete
    /// away from every other directory that can live beside them.
    /// </remarks>
    /// <param name="name">Directory name.</param>
    /// <returns>True when the name is a job id.</returns>
    internal static bool IsJobScratchDirectory(string? name)
        => !string.IsNullOrEmpty(name)
           && name.Length == 32
           && name.All(character => (character >= '0' && character <= '9') || (character >= 'a' && character <= 'f'));

    /// <summary>
    /// Asks whether a path is inside a root directory, resolved (B23).
    /// </summary>
    /// <param name="rootFull">Fully resolved root.</param>
    /// <param name="path">Path to test.</param>
    /// <returns>True when the path is the root itself or below it.</returns>
    internal static bool IsInsideRoot(string rootFull, string path)
    {
        try
        {
            var candidate = Path.GetFullPath(path);
            var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;
            return candidate.StartsWith(prefix, StringComparison.Ordinal);
        }
        catch (Exception)
        {
            return false;
        }
    }

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
        if (match.Success && int.TryParse(match.Groups[1].Value, out var percent))
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

    /// <summary>
    /// Checks if a completed sync job exists for the given item and subtitle index.
    /// </summary>
    private bool HasCompletedSync(Guid itemId, int subtitleIndex)
    {
        return _jobs.Values.Any(j =>
            j.ItemId == itemId &&
            j.SubtitleIndex == subtitleIndex &&
            j.Status == SyncJobStatus.Completed);
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
        var trackedAtStart = _liveProcesses.Count;

        foreach (var entry in _jobCancellation)
        {
            try { entry.Value.Cancel(); }
            catch (ObjectDisposedException) { /* the run finished first */ }
        }

        var (childrenAsked, childrenStopped) = KillChildProcesses();
        var tasksWaited = _laneTasks.Count + (_pumpTask is null ? 0 : 1);
        var tasksDone = WaitForLanes(ShutdownWaitMs);
        var trackedLeft = JobProcessRegistry.LiveProcesses;
        var childrenLeft = _liveProcesses.Values.Count(process => !SafeHasExited(process));

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
    /// Which queued subtitles of this file should be extracted along with this one.
    /// </summary>
    /// <remarks>
    /// Reading a file's clusters once and taking every requested subtitle out of them is what makes a
    /// multi-language episode affordable: the alternative visits the same clusters once per language.
    /// Only embedded tracks are listed (an external subtitle is already a file of its own), and the
    /// list is capped so one very large batch does not hold every track's text in memory at once.
    /// </remarks>
    /// <param name="videoPath">The media file being read.</param>
    /// <param name="primaryOrdinal">The ordinal of the subtitle this job is extracting.</param>
    /// <returns>Ordinals to extract in one pass, the primary first.</returns>
    private IReadOnlyList<int> SiblingOrdinals(string videoPath, int primaryOrdinal)
    {
        const int MaxTracksPerPass = 48;
        var wanted = new List<int> { primaryOrdinal };
        lock (_queueLock)
        {
            foreach (var other in _runOrder)
            {
                if (wanted.Count >= MaxTracksPerPass)
                {
                    break;
                }

                if (other.Status != SyncJobStatus.Queued
                    || !_jobContexts.TryGetValue(other.Id, out var context)
                    || context.Stream.IsExternal
                    || !string.Equals(context.Video.Path, videoPath, StringComparison.Ordinal))
                {
                    continue;
                }

                if (!wanted.Contains(context.Ordinal))
                {
                    wanted.Add(context.Ordinal);
                }
            }
        }

        return wanted;
    }

    /// <summary>Looks up an already extracted track, taking it out so the text is freed after use.</summary>
    /// <param name="videoPath">The media file.</param>
    /// <param name="ordinal">Subtitle ordinal within the file.</param>
    /// <param name="text">The extracted SRT, when it was cached.</param>
    /// <returns>True when it was found.</returns>
    private bool TryTakeExtracted(string videoPath, int ordinal, out string text) =>
        _extractedText.TryRemove(ExtractedKey(videoPath, ordinal), out text!);

    /// <summary>Looks up an already extracted track without consuming it.</summary>
    /// <remarks>
    /// The reference track is not the job's own subtitle: two jobs of the same file may need the same
    /// text, so this one peeks instead of taking. Taking it (as the job's own extraction does) makes
    /// the second reader fall through to the container index for text this process already holds.
    /// </remarks>
    /// <param name="videoPath">The media file.</param>
    /// <param name="ordinal">Subtitle ordinal within the file.</param>
    /// <param name="text">The extracted SRT, when it was cached.</param>
    /// <returns>True when it was found.</returns>
    private bool TryPeekExtracted(string videoPath, int ordinal, out string text) =>
        _extractedText.TryGetValue(ExtractedKey(videoPath, ordinal), out text!);

    /// <summary>
    /// Reads the text of the track a job will align against, from the cheapest source that has it.
    /// </summary>
    /// <remarks>
    /// The order is deliberate and ends without a whole-file ffmpeg read: this run's memory (the lane
    /// or a sibling job already produced the track), the extracted-subtitle cache (an earlier run
    /// did), then the container's own index. Anything that is not already text is therefore read
    /// through the index, never by demuxing the container with ffmpeg — that cost, per job, is what
    /// stops a bulk run finishing. Returning null is a legitimate answer: the caller then aligns
    /// against the audio instead.
    /// </remarks>
    /// <param name="videoPath">The media file.</param>
    /// <param name="ordinal">0-based subtitle ordinal of the reference track.</param>
    /// <param name="job">Job whose phase is updated while reading.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The SRT text, or null when no reader could produce it.</returns>
    private async Task<string?> TryReadReferenceTextAsync(
        string videoPath,
        int ordinal,
        SyncJob job,
        CancellationToken cancellationToken)
    {
        if (TryPeekExtracted(videoPath, ordinal, out var inMemory) && !string.IsNullOrWhiteSpace(inMemory))
        {
            PluginLog.Info(
                $"reference: method=reused cues={SrtWriter.CountCues(inMemory)} file={videoPath} stream={ordinal}");
            return inMemory;
        }

        if (SubtitleCache.TryGet(videoPath, ordinal.ToString(CultureInfo.InvariantCulture), out var onDisk)
            && !string.IsNullOrWhiteSpace(onDisk))
        {
            PluginLog.Info(
                $"reference: method=cache cues={SrtWriter.CountCues(onDisk)} file={videoPath} stream={ordinal}");
            return onDisk;
        }

        var reason = string.Empty;
        string? text = null;
        var stats = new MkvExtractionStats();

        try
        {
            if (MkvSubtitleExtractor.LooksLikeMatroska(videoPath))
            {
                var watch = System.Diagnostics.Stopwatch.StartNew();
                var progress = new Action<string>(line =>
                {
                    job.Phase = "Reading the reference subtitle: " + line;
                });
                var ok = await Task.Factory.StartNew(
                    () => MkvSubtitleExtractor.TryExtract(
                        videoPath,
                        ordinal,
                        out text,
                        out reason,
                        progress,
                        out stats,
                        null,
                        null,
                        cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);
                watch.Stop();

                if (!ok || string.IsNullOrWhiteSpace(text))
                {
                    PluginLog.Warn(
                        $"reference: method=index-none ms={watch.ElapsedMilliseconds} stream={ordinal} "
                        + $"reason={reason} file={videoPath}");
                    return null;
                }

                PluginLog.Info(
                    $"reference: method={stats.Method} ms={watch.ElapsedMilliseconds} cues={SrtWriter.CountCues(text)} "
                    + $"bytesRead={stats.BytesRead} readCalls={stats.ReadCalls} file={videoPath} stream={ordinal}");
            }
            else if (Mp4SubtitleExtractor.LooksLikeMp4(videoPath))
            {
                if (!Mp4SubtitleExtractor.TryExtract(videoPath, ordinal, out var mp4Text, out reason)
                    || string.IsNullOrWhiteSpace(mp4Text))
                {
                    PluginLog.Warn(
                        $"reference: method=mp4-none stream={ordinal} reason={reason} file={videoPath}");
                    return null;
                }

                text = mp4Text;
                PluginLog.Info(
                    $"reference: method=mp4-sample-table cues={SrtWriter.CountCues(text)} file={videoPath} stream={ordinal}");
            }
            else
            {
                PluginLog.Warn($"reference: method=none stream={ordinal} reason=no index reader matched this container");
                return null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"reference: method=index-error stream={ordinal} reason={ex.Message} file={videoPath}");
            return null;
        }

        // Keep it for the other subtitles of this file (and the next run): the reference is read once.
        CacheExtracted(videoPath, ordinal, text!);
        SubtitleCache.Store(videoPath, ordinal.ToString(CultureInfo.InvariantCulture), text!);
        return text;
    }

    /// <summary>Remembers an extracted track for the jobs that follow it.</summary>
    /// <param name="videoPath">The media file.</param>
    /// <param name="ordinal">Subtitle ordinal within the file.</param>
    /// <param name="text">The extracted SRT.</param>
    private void CacheExtracted(string videoPath, int ordinal, string text)
    {
        var key = ExtractedKey(videoPath, ordinal);
        _extractedText[key] = text;
        _extractedOrder.Enqueue(key);
        while (_extractedOrder.Count > ExtractedCacheLimit && _extractedOrder.TryDequeue(out var oldest))
        {
            _extractedText.TryRemove(oldest, out _);
        }
    }

    private static string ExtractedKey(string videoPath, int ordinal) => ordinal + "\u0000" + videoPath;

    /// <summary>
    /// Extracts an embedded subtitle using the cheapest applicable method, and reports which
    /// one worked:
    ///
    /// 1. the container's own index — Matroska <c>Cues</c>, MP4 <c>stbl</c> (kilobytes read);
    /// 2. ffmpeg, which demuxes the whole file (the only option for exotic codecs or files
    ///    without an index), guarded by a timeout so a stuck or very slow read fails with a
    ///    useful message instead of looking like a hang.
    ///
    /// Every skipped method logs why, so a slow path can always be traced back to its cause.
    /// </summary>
    /// <param name="videoPath">Media file.</param>
    /// <param name="subtitleOrdinal">0-based index among subtitle streams.</param>
    /// <param name="containerIndex">Real container stream index (for ffmpeg -map).</param>
    /// <param name="outputPath">Where to write the SRT.</param>
    /// <param name="config">Plugin configuration.</param>
    /// <param name="job">Job whose phase/progress is updated while extracting.</param>
    /// <param name="runTimeTicks">Total runtime from Jellyfin, used to turn ffmpeg's timestamps into progress.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The method that produced the subtitle ("matroska-cues", "mp4-sample-table" or "ffmpeg").</returns>
    private async Task<string> ExtractEmbeddedAsync(
        string videoPath,
        int subtitleOrdinal,
        int containerIndex,
        string outputPath,
        Configuration.PluginConfiguration config,
        SyncJob job,
        long? runTimeTicks,
        CancellationToken cancellationToken)
    {
        var utf8 = new System.Text.UTF8Encoding(false);
        var skipped = new List<string>();

        if (MkvSubtitleExtractor.LooksLikeMatroska(videoPath))
        {
            job.Phase = "Extracting subtitle from the Matroska index";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Action<string>(line =>
            {
                job.Phase = "Extracting subtitle: " + line;

                // Advance the bar with the work actually done. Without this the whole extraction
                // sat at a frozen 5%, which made four independent workers look synchronised.
                if (ExtractionFraction(line) is { } fraction)
                {
                    job.Progress = 0.05 + (0.15 * fraction); // 5% → 20% is the extraction window
                }
            });
            // The extraction is synchronous, blocking IO on the media share, and it runs for tens of
            // seconds. On a pool thread it starves everything else the server is doing - including
            // the request that is still queueing the rest of the batch, which is why a 50-task batch
            // trickled in a few tasks every few seconds and the scheduler never saw a full queue. A
            // dedicated thread costs nothing here and leaves the pool for requests.
            var extractedText = string.Empty;
            var extractionReason = string.Empty;
            var extractionStats = new MkvExtractionStats();
            var extracted = false;

            // This track may already have been produced while the file was read for another language.
            if (TryTakeExtracted(videoPath, subtitleOrdinal, out var cachedText))
            {
                PluginLog.Info(
                    $"extract: method=reused cues={SrtWriter.CountCues(cachedText)} cacheLeft={_extractedText.Count} file={videoPath} stream={subtitleOrdinal}");
                await File.WriteAllTextAsync(outputPath, cachedText, utf8, cancellationToken).ConfigureAwait(false);
                return "matroska-cached";
            }

            // ...or while the file was read on a previous run. This is the whole answer to extraction
            // being slow: it is paid once per file, not once per run. The cache holds the subtitle as
            // it came out of the container, so the sync itself still runs (it depends on settings and
            // the engine), but the file is not read again.
            if (SubtitleCache.TryGet(videoPath, subtitleOrdinal.ToString(CultureInfo.InvariantCulture), out var diskText))
            {
                PluginLog.Info(
                    $"extract: method=cache cues={SrtWriter.CountCues(diskText)} file={videoPath} stream={subtitleOrdinal} "
                    + $"(no read: this file's subtitle was extracted on an earlier run)");
                await File.WriteAllTextAsync(outputPath, diskText, utf8, cancellationToken).ConfigureAwait(false);
                return "subtitle-cache";
            }

            // The extraction lane is what reads files: it produces every queued subtitle of a file in
            // one pass and stores each as it arrives, and the scheduler only starts a job once its
            // subtitle is there. Reaching this point with a cache miss means the lane is not running
            // (disposed, or it died), so the job extracts for itself - as a whole-file pass serving its
            // siblings if they are queued too, because that is cheaper than one pass per language.
            var wanted = SiblingOrdinals(videoPath, subtitleOrdinal);
            if (wanted.Count > 1)
            {
                job.Phase = "Extracting " + wanted.Count + " subtitles from the Matroska index in one pass";
                var many = new Dictionary<int, string>();
                var manyReason = string.Empty;
                var manyStats = new MkvExtractionStats();
                var manyOk = await Task.Factory.StartNew(
                    () => MkvSubtitleExtractor.TryExtractMany(
                        videoPath,
                        wanted,
                        out many,
                        out manyReason,
                        out manyStats,
                        cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);

                foreach (var pair in many)
                {
                    CacheExtracted(videoPath, pair.Key, pair.Value);
                    SubtitleCache.Store(videoPath, pair.Key.ToString(CultureInfo.InvariantCulture), pair.Value);
                }

                SubtitleCache.Prune();

                PluginLog.Info(
                    $"extract: method=shared-pass ms={watch.ElapsedMilliseconds} tracks={many.Count}/{wanted.Count} "
                    + $"cues={SrtWriter.CountCues(many.TryGetValue(subtitleOrdinal, out var own) ? own : string.Empty)} "
                    + $"bytesRead={manyStats.BytesRead} readCalls={manyStats.ReadCalls} "
                    + $"clusters={manyStats.ClustersVisited} blocks={manyStats.SubtitleBlocks} alsoBlocks={manyStats.AlsoBlocks} "
                    + $"blockOffsets={manyStats.BlockOffsets} "
                    + $"plan={manyStats.Route} expected={manyStats.PlanExpectedBytes} expectedCalls={manyStats.PlanExpectedCalls} "
                    + $"missed={manyStats.PlanMissed} missedTracks={string.Join(",", manyStats.MissedTracks)} "
                    + $"bytesTwice={manyStats.BytesReadTwice} "
                    + $"prefetched={manyStats.PrefetchedRanges} unusedPrefetch={manyStats.PrefetchedUnusedRanges} "
                    + $"memoryReads={manyStats.MemoryServedReads} msPerRead={manyStats.MeasuredMsPerRead:0.00} "
                    + $"mbPerSecond={manyStats.MeasuredMbPerSecond:0.0} ok={manyOk} reason={manyReason} file={videoPath}");

                // A pass the user killed is not a reason to start another one (B20): the chain used to carry on to
                // the per-track reader and then to ffmpeg, doing minutes of work on a file just stopped, and ending
                // with the job marked failed.
                if (!manyOk)
                {
                    StopChainIfCancelled(manyReason, cancellationToken, "shared pass");
                }

                // Which track a shared pass could not produce is something the log has to say (B2): the call
                // succeeds with a gap in it otherwise, and the gap may be the language someone is waiting for.
                if (manyStats.MissedTracks.Count > 0)
                {
                    PluginLog.Warn(
                        $"extract: shared pass produced no subtitle for track(s) "
                        + $"{string.Join(", ", manyStats.MissedTracks)} of {string.Join(", ", wanted)} "
                        + $"(served {many.Count}/{wanted.Count}) file={videoPath}");
                    _logger.LogWarning(
                        "The pass that read {Video} produced no subtitle for track(s) {Missed} of {Wanted}",
                        videoPath, string.Join(", ", manyStats.MissedTracks), string.Join(", ", wanted));
                    job.ExtractionNote = (job.ExtractionNote is null ? string.Empty : job.ExtractionNote + "; ")
                        + $"no subtitle for track(s) {string.Join(", ", manyStats.MissedTracks)}";
                }

                if (manyOk && many.TryGetValue(subtitleOrdinal, out var sharedText) && sharedText.Length > 0)
                {
                    // The line above is the whole record of this extraction: it was one pass over the
                    // file and it served every queued language. The generic line further down used to
                    // follow it with the same numbers under a different method name, which read like a
                    // second pass had run - two lines per pass, one of them a duplicate.
                    await File.WriteAllTextAsync(outputPath, sharedText, utf8, cancellationToken).ConfigureAwait(false);
                    return "matroska-shared";
                }
            }

            if (!extracted)
            {
                extractedText = string.Empty;
                extractionReason = string.Empty;
                extractionStats = new MkvExtractionStats();
                extracted = await Task.Factory.StartNew(
                    () => MkvSubtitleExtractor.TryExtract(
                        videoPath,
                        subtitleOrdinal,
                        out extractedText,
                        out extractionReason,
                        progress,
                        out extractionStats,
                        null,
                        null,
                        cancellationToken),
                    cancellationToken,
                    TaskCreationOptions.LongRunning,
                    TaskScheduler.Default).ConfigureAwait(false);
            }

            var srt = extractedText;
            var why = extractionReason;
            var stats = extractionStats;
            if (extracted)
            {
                SubtitleCache.Store(videoPath, subtitleOrdinal.ToString(CultureInfo.InvariantCulture), srt);
                await File.WriteAllTextAsync(outputPath, srt, utf8, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Extracted embedded subtitle in {Ms} ms ({Cues} cues, {Stats}) from {Video}",
                    watch.ElapsedMilliseconds, SrtWriter.CountCues(srt), stats, videoPath);
                PluginLog.Info($"extract: method={stats.Method} ms={watch.ElapsedMilliseconds} cues={SrtWriter.CountCues(srt)} bytesRead={stats.BytesRead} readCalls={stats.ReadCalls} clusters={stats.ClustersVisited} blocks={stats.SubtitleBlocks} file={videoPath}");
                return stats.Method;
            }

            // A cancelled read is not "no subtitle in this file" (B20): stop here instead of trying the next reader.
            StopChainIfCancelled(why, cancellationToken, "Matroska index pass");
            skipped.Add("matroska-index: " + why + " [" + stats + "]");
        }

        if (Mp4SubtitleExtractor.LooksLikeMp4(videoPath))
        {
            job.Phase = "Extracting subtitle with the MP4 sample table";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (Mp4SubtitleExtractor.TryExtract(videoPath, subtitleOrdinal, out var srt, out var why))
            {
                await File.WriteAllTextAsync(outputPath, srt, utf8, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Extracted embedded subtitle via the MP4 sample table in {Ms} ms ({Cues} cues) from {Video}",
                    watch.ElapsedMilliseconds, SrtWriter.CountCues(srt), videoPath);
                return "mp4-sample-table";
            }

            StopChainIfCancelled(why, cancellationToken, "MP4 sample-table pass");
            skipped.Add("mp4-sample-table: " + why);
        }

        if (skipped.Count == 0)
        {
            skipped.Add("no index reader matched this container");
        }

        double sizeMb = 0;
        try
        {
            sizeMb = new FileInfo(videoPath).Length / (1024.0 * 1024.0);
        }
        catch (Exception)
        {
            // Size is only used for the log line.
        }

        var timeout = TimeSpan.FromMinutes(Math.Clamp(config.ExtractionTimeoutMinutes, 1, 240));
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        var fallbackWatch = System.Diagnostics.Stopwatch.StartNew();

        _logger.LogInformation(
            "Falling back to ffmpeg extraction for {Video} ({Size:0} MB, up to {Minutes} min) — indexed reads not usable: {Reasons}",
            videoPath, sizeMb, timeout.TotalMinutes,
            skipped.Count == 0 ? "no index reader matched this container" : string.Join("; ", skipped));

        // The single most useful line for a slow extraction: which reader refused the track and why.
        PluginLog.Warn(
            $"extract fallback to ffmpeg: file={videoPath} sizeMb={sizeMb:0.0} timeoutMinutes={timeout.TotalMinutes:0} reasons={string.Join("; ", skipped)}");

        // Say what is happening *before* the slow path starts: a whole-file ffmpeg read can
        // take minutes, and a frozen "Extracting subtitle" at 5% tells the user nothing.
        var durationSeconds = runTimeTicks.HasValue && runTimeTicks.Value > 0
            ? runTimeTicks.Value / (double)TimeSpan.TicksPerSecond
            : 0;
        job.Phase = $"Extracting subtitle with ffmpeg"
            + (sizeMb >= 1 ? $" — reading {sizeMb:0} MB" : string.Empty)
            + (durationSeconds > 0 ? $", up to {timeout.TotalMinutes:0} min" : string.Empty);
        _logger.LogInformation(
            "Extracting subtitle with ffmpeg (whole-file demux) for {Video}: {Size:0} MB, duration {Duration:0}s",
            videoPath, sizeMb, durationSeconds);

        try
        {
            await ExtractSubtitleWithProgressAsync(
                videoPath, containerIndex, outputPath, durationSeconds, job, timeoutCts.Token).ConfigureAwait(false);
            _logger.LogInformation(
                "ffmpeg extraction finished in {Ms} ms for {Video}", fallbackWatch.ElapsedMilliseconds, videoPath);
            PluginLog.Info($"extract: method=ffmpeg ms={fallbackWatch.ElapsedMilliseconds} file={videoPath}");
            return "ffmpeg";
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"Subtitle extraction timed out after {timeout.TotalMinutes:0} minutes for a {sizeMb:0} MB file. "
                + $"Indexed extraction could not be used ({string.Join("; ", skipped)}), so ffmpeg had to read the whole file — "
                + "this usually means the media sits on a slow or busy mount, or the file needs remuxing to carry an index.");
        }
    }

    /// <summary>
    /// Runs the ffmpeg extraction while translating its reported timestamps into job
    /// progress (5% → 20%), so the UI shows movement instead of a stalled bar.
    /// </summary>
    /// <remarks>
    /// This is the last resort for a track no index reader could produce, and the one extraction whose output
    /// cannot be checked against anything else: whatever it writes is handed on as the subtitle. So what it
    /// wrote is checked instead (B8, <see cref="ExtractionOutputGuard"/>), and a run that failed, was killed or
    /// read a cut-short file leaves nothing behind - a partial subtitle is silently wrong in every way that
    /// matters (cues missing, the tail of the film untimed) and the engine cannot tell it from a complete one.
    /// </remarks>
    /// <param name="videoPath">Media file to demux.</param>
    /// <param name="streamIndex">Real container stream index to map.</param>
    /// <param name="outputPath">Where the SRT is written.</param>
    /// <param name="durationSeconds">Media duration, used to turn ffmpeg's progress into a fraction.</param>
    /// <param name="job">Job whose phase and progress are updated.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>How many cues the verified extraction produced.</returns>
    internal async Task<int> ExtractSubtitleWithProgressAsync(
        string videoPath,
        int streamIndex,
        string outputPath,
        double durationSeconds,
        SyncJob job,
        CancellationToken cancellationToken)
    {
        var ffmpegPath = ResolveFfmpegPath();
        var args = new List<string>
        {
            "-y",
            "-nostdin",
            "-i", videoPath,
            "-map", $"0:{streamIndex}",
            "-f", "srt",
            "-progress", "pipe:2",
            "-nostats",
            outputPath
        };

        // ffmpeg's own account of the input: the truncation markers live in these lines, and on a cut-short
        // container they are the only sign that the SRT is a prefix of the track (exit code 0).
        var stderrTail = new List<string>();
        var lastReported = -1.0;
        var exitCode = 0;
        var processEnded = false;

        try
        {
            exitCode = await RunProcessWithStderrCallbackAsync(
                ffmpegPath,
                args,
                null,
                line =>
                {
                    // Undiscriminating and bounded: the whole line goes to the guard, and a progress line is
                    // cheap to keep. Truncation is reported at the moment the file ends, so a tail is enough
                    // and a marker that repeats stays in it.
                    lock (stderrTail)
                    {
                        stderrTail.Add(line);
                        if (stderrTail.Count > ExtractionStderrTailLines)
                        {
                            stderrTail.RemoveAt(0);
                        }
                    }

                    if (durationSeconds <= 0)
                    {
                        return;
                    }

                    var seconds = ParseFfmpegProgressSeconds(line);
                    if (seconds < 0)
                    {
                        return;
                    }

                    var fraction = Math.Min(1.0, seconds / durationSeconds);
                    if (fraction - lastReported < 0.01)
                    {
                        return;
                    }

                    lastReported = fraction;
                    job.Progress = 0.05 + (0.15 * fraction); // 5% → 20% is the extraction window
                    job.Phase = $"Extracting subtitle with ffmpeg — {fraction * 100:0}% of the file read";
                },
                cancellationToken,
                jobId: job.Id).ConfigureAwait(false);
            processEnded = true;
        }
        catch (OperationCanceledException)
        {
            // Cancelled or past the extraction timeout: ffmpeg was killed mid-write, so what is on disk is a
            // prefix of the subtitle. The caller turns this into a failure (or a kill); the file goes first.
            DiscardPartialExtraction(job, outputPath, "the extraction was stopped before it finished");
            throw;
        }
        finally
        {
            if (!processEnded)
            {
                DiscardPartialExtraction(job, outputPath, "the extraction did not run to completion");
            }
        }

        var verdict = ExtractionOutputGuard.Judge(exitCode, outputPath, stderrTail);
        if (!verdict.Accept)
        {
            PluginLog.Warn(
                $"extract: rejected file={videoPath} stream={streamIndex} exit={exitCode} "
                + $"reason={verdict.Reason}");
            throw new InvalidOperationException("ffmpeg subtitle extraction failed: " + verdict.Reason);
        }

        return verdict.Cues;
    }

    /// <summary>
    /// Throws away what a stopped extraction wrote, and says so in the plugin's own log.
    /// </summary>
    /// <remarks>
    /// Nothing downstream may see a partial extraction: the file is the engine's input, so a prefix of the
    /// track produces a confidently wrong offset rather than an error.
    /// </remarks>
    /// <param name="job">The job the extraction belonged to.</param>
    /// <param name="outputPath">Where the extraction was writing.</param>
    /// <param name="why">Why it is being discarded.</param>
    private static void DiscardPartialExtraction(SyncJob job, string outputPath, string why)
    {
        var note = ExtractionOutputGuard.Discard(outputPath);
        PluginLog.Warn($"[{job.Id}] {why} - {note}");
    }

    /// <summary>
    /// Reads the processed timestamp from one line of ffmpeg's <c>-progress</c> output
    /// (<c>out_time_us=…</c>), falling back to the human-readable <c>out_time=HH:MM:SS</c>.
    /// </summary>
    /// <remarks>
    /// <c>out_time_ms</c> is <b>microseconds</b> despite its name (B19): measured with ffmpeg 7.1 on this
    /// project's fixture, the same moment is printed as <c>out_time_us=33000000</c>,
    /// <c>out_time_ms=33000000</c> and <c>out_time=00:00:33.000000</c>. Dividing that field by 1 000 turned
    /// 33 seconds into 33 000, which the extraction window clamps to "100 % of the file read" - so a pass
    /// alternated between the true fraction and a full bar once per progress block, and the phase text a user
    /// reads could be the wrong one.
    /// </remarks>
    /// <param name="line">One progress line.</param>
    /// <returns>Seconds processed, or -1 when the line is not a progress line.</returns>
    internal static double ParseFfmpegProgressSeconds(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.StartsWith("out_time_us=", StringComparison.Ordinal)
            && long.TryParse(trimmed[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
        {
            return micros / 1_000_000.0;
        }

        // Misnamed by ffmpeg and microseconds in fact; see the remarks above for the measurement.
        if (trimmed.StartsWith("out_time_ms=", StringComparison.Ordinal)
            && long.TryParse(trimmed[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var microsFromMs))
        {
            return microsFromMs / 1_000_000.0;
        }

        if (trimmed.StartsWith("out_time=", StringComparison.Ordinal))
        {
            var match = System.Text.RegularExpressions.Regex.Match(
                trimmed, @"out_time=(\d+):(\d+):(\d+(?:\.\d+)?)");
            if (match.Success)
            {
                return (int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
                    + (int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
                    + double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
            }
        }

        return -1;
    }

    /// <summary>Friendly name of an extraction method, for the UI phase and logs.</summary>
    private static string DescribeExtraction(string method) => method switch
    {
        "seekhead-cues" => "from the Matroska index (SeekHead)",
        "matroska-shared" => "from the pass that read this file for its other languages",
        "cue-index" => "from the Matroska cue index",
        "metadata-scan" => "by walking the file's block headers (this file's index does not point at its subtitle blocks)",
        "matroska-cues" => "from the Matroska index",
        "matroska-cached" => "from the pass that already read this file",
        "subtitle-cache" => "from the extracted-subtitle cache (no read this run)",
        "mp4-sample-table" => "with the MP4 sample table",
        "ffmpeg" => "with ffmpeg (whole-file read)",
        "cancelled" => "stopped by a kill",
        _ => "by a reader this build cannot name (" + method + ")"
    };

    private async Task ExtractSubtitle(string videoPath, int streamIndex, string outputPath, CancellationToken cancellationToken = default)
    {
        var ffmpegPath = ResolveFfmpegPath();

        // ArgumentList passes argv directly — no string-quoting/escaping layer
        // that can mangle paths into "Error opening output files: Invalid argument".
        //
        // streamIndex is the REAL container stream index discovered by probing
        // the file (ResolveContainerSubtitleIndexAsync) — mapped with plain
        // "-map 0:N". Never derive it from Jellyfin's MediaStream.Index or from
        // counting subtitle streams in Jellyfin's MediaStreams list: both have
        // been observed to disagree with the actual container (external sidecar
        // tracks and renumbered streams), producing "Failed to set value '0:N'
        // for option 'map': Invalid argument".
        var args = new List<string>
        {
            "-y",
            "-nostdin",
            "-i", videoPath,
            "-map", $"0:{streamIndex}",
            "-f", "srt",
            outputPath
        };

        _logger.LogInformation("Extracting subtitle: ffmpeg {Args}", string.Join(" ", args));

        var (exitCode, stderr) = await RunProcessArgumentListAsync(ffmpegPath, args, null, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg subtitle extraction failed: {FfmpegError(stderr)}");
        }
    }

    private static string FfmpegError(string stderr)
    {
        if (string.IsNullOrWhiteSpace(stderr))
        {
            return "exit code from ffmpeg (no stderr captured)";
        }

        // Last up-to-three non-empty lines usually contain the real reason.
        var lines = stderr.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToArray();

        var tail = string.Join(" | ", lines.Skip(Math.Max(0, lines.Length - 3)));
        return string.IsNullOrEmpty(tail) ? "unknown ffmpeg error" : tail;
    }

    private async Task<(int ExitCode, string Stderr)> RunProcessArgumentListAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDir, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (workingDir is not null)
        {
            process.StartInfo.WorkingDirectory = workingDir;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        process.Start();
        TrackChildProcess(process);

        // Kill the process if cancellation is requested
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _liveProcesses.TryRemove(process.Id, out _);
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        await stdoutTask.ConfigureAwait(false);

        return (process.ExitCode, stderr);
    }

    private async Task<(int ExitCode, string Stdout, string Stderr)> RunCapturedAsync(
        string executable, string arguments, string? workingDir, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (workingDir is not null)
        {
            process.StartInfo.WorkingDirectory = workingDir;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        process.Start();
        TrackChildProcess(process);

        // Kill the process if cancellation is requested
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _liveProcesses.TryRemove(process.Id, out _);
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);

        return (process.ExitCode, stdout, stderr);
    }

    private async Task<int> RunProcessAsync(string executable, string arguments, string? workingDir, CancellationToken cancellationToken)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (workingDir is not null)
        {
            process.StartInfo.WorkingDirectory = workingDir;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        process.Start();
        TrackChildProcess(process);

        // Kill the process if cancellation is requested
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _liveProcesses.TryRemove(process.Id, out _);
        }

        var stderr = await stderrTask.ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            _logger.LogWarning("Process {Exe} exited with code {Code}. stderr: {Stderr}", executable, process.ExitCode, stderr);
        }
        else
        {
            _logger.LogDebug("Process {Exe} completed. stdout: {Stdout}", executable, stdout);
        }

        return process.ExitCode;
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

        var exitCode = await RunProcessWithStderrCallbackAsync(
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

        var measured = File.Exists(verifyOutput) ? MeasureSyncChange(stretchedInput, verifyOutput) : null;
        var ratio = measured?.Ratio ?? 1.0;
        var shiftMs = measured?.ShiftMs ?? 0;
        if (AlignmentHoldsAgainstAudio(ratio, shiftMs, videoSeconds))
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

        var fallbackExit = await RunProcessWithStderrCallbackAsync(
            ffsubsyncExe, fallbackArgs, tempDir, null, cancellationToken).ConfigureAwait(false);
        if (fallbackExit == 0 && File.Exists(fallbackOutput))
        {
            return (fallbackOutput, true, originalInput);
        }

        return (null, true, originalInput);
    }

    // The per-job cancellation line (S23). Cancelling used to leave only the batch-level count, so the
    // only way to reconcile a cancelled batch from the plugin log was to take that number on trust.
    // Same shape as the completed and failed lines, so every job in a batch has one terminal line of its
    // own whatever happened to it.
    private static void LogPluginCancellation(SyncJob job)
        => PluginLog.Info(
            $"job {job.Id} cancelled: mode={job.Mode} item={job.ItemId} stream={job.SubtitleIndex}");

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
    private async Task<int> RunProcessWithStderrCallbackAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDir,
        Action<string>? onStderrLine, CancellationToken cancellationToken, EngineWatch? watch = null,
        string? jobId = null)
    {
        var owner = jobId ?? watch?.JobId;
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        if (workingDir is not null)
        {
            process.StartInfo.WorkingDirectory = workingDir;
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        process.Start();
        TrackChildProcess(process);
        if (owner is not null)
        {
            JobProcessRegistry.Begin(owner);
        }

        // S27: while this process runs, say so in the plugin's own log. Started once the process is
        // live and disposed when it exits, so no line can ever describe a process that is already gone.
        using var heartbeat = watch is null ? null : new EngineHeartbeat(watch);

        // Kill the process if cancellation is requested
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        // Read stdout in background
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        // Read stderr line-by-line in real-time
        var stderrTask = Task.Run(async () =>
        {
            using var reader = process.StandardError;
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    // End of the stream is the loop's own exit; EndOfStream is sync-over-async on
                    // .NET 10 (CA2024) and blocks a thread of the pool while we await a line.
                    break;
                }

                if (owner is not null)
                {
                    // A line from the process is the process saying it is working: this is what dates its
                    // silence for the wedged-process rule (B6).
                    JobProcessRegistry.SawOutput(owner);
                }

                onStderrLine?.Invoke(line);
            }
        }, cancellationToken);

        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _liveProcesses.TryRemove(process.Id, out _);
            if (owner is not null)
            {
                JobProcessRegistry.End(owner);
            }
        }

        await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false);

        return process.ExitCode;
    }

    /// <summary>
    /// Runs a process and returns (exitCode, combined stdout+stderr output).
    /// </summary>
    private async Task<(int ExitCode, string Output)> RunProcessCaptureAsync(string executable, string arguments, string? workingDir)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = executable,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        if (workingDir is not null)
        {
            process.StartInfo.WorkingDirectory = workingDir;
        }

        process.Start();
        TrackChildProcess(process);

        try
        {
            var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
            var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

            await process.WaitForExitAsync().ConfigureAwait(false);

            var output = string.Concat(stdout, stderr).Trim();
            return (process.ExitCode, output);
        }
        finally
        {
            // Registered like the other four runners (B14). This one was the exception, and it covers the provisioning
            // and probe steps - `ffsubsync --version`, `python3 -m venv`, `pip install`, `apt-get install` - any of
            // which can run for minutes, so a kill or a teardown had no handle on them.
            _liveProcesses.TryRemove(process.Id, out _);
        }
    }

    /// <summary>
    /// Ensures a system python3 with venv + ensurepip support exists, installing
    /// it via apt-get when missing. The managed ffsubsync virtualenv cannot be
    /// created without it, and the official Jellyfin Docker image ships neither
    /// python3 nor the Debian python3-venv package. Requires root for the
    /// apt-get path (the Docker default); non-root processes get a clear error.
    /// </summary>
    /// <exception cref="InvalidOperationException">Python could not be made available.</exception>
    private async Task EnsurePythonAvailableAsync()
    {
        if (await IsPythonVenvReadyAsync().ConfigureAwait(false))
        {
            return;
        }

        _logger.LogInformation("python3 with venv support is missing; attempting automatic installation via apt-get");

        var (uidExit, uidOutput) = await RunProcessCaptureAsync("id", "-u", null).ConfigureAwait(false);
        var isRoot = uidExit == 0 && uidOutput.Trim() == "0";
        if (!isRoot)
        {
            throw new InvalidOperationException(
                "python3 with venv support is missing and the Jellyfin process is not running as root, " +
                "so the plugin cannot install it automatically. Run Jellyfin as root (the default in the " +
                "official Docker image) and retry, or install it manually with: " +
                "apt-get install -y python3 python3-venv");
        }

        var (updateExit, updateOutput) = await RunProcessCaptureAsync("apt-get", "update", null).ConfigureAwait(false);
        if (updateExit != 0)
        {
            _logger.LogWarning("apt-get update failed during python3 provisioning (continuing anyway): {Output}", Truncate(updateOutput, 800));
        }

        var (installExit, installOutput) = await RunProcessCaptureAsync(
            "apt-get",
            "install -y --no-install-recommends python3 python3-venv",
            null).ConfigureAwait(false);

        if (installExit != 0 || !await IsPythonVenvReadyAsync().ConfigureAwait(false))
        {
            throw new InvalidOperationException(
                $"Automatic python3 installation failed ({Truncate(installOutput, 800)}). " +
                "Install it manually with: apt-get install -y python3 python3-venv");
        }

        _logger.LogInformation("python3 and python3-venv installed successfully");
    }

    /// <summary>
    /// Probes whether a usable python3 (with the venv and ensurepip modules) is available.
    /// </summary>
    private async Task<bool> IsPythonVenvReadyAsync()
    {
        try
        {
            var (exitCode, _) = await RunProcessCaptureAsync("python3", $"-c {EscapeArg("import sys, venv, ensurepip")}", null).ConfigureAwait(false);
            return exitCode == 0;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // python3 itself is not installed.
            return false;
        }
    }

    private static string Truncate(string value, int maxLength)
        => string.IsNullOrEmpty(value) || value.Length <= maxLength ? value : value[^maxLength..];

    private static string EscapeArg(string arg)
    {
        if (arg.Contains(' ') || arg.Contains('"') || arg.Contains('\''))
        {
            return $"\"{arg.Replace("\"", "\\\"")}\"";
        }

        return arg;
    }
}
