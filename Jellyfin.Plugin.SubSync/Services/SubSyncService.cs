using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Jellyfin.Plugin.SubSync.Configuration;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Logging;

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
    /// <summary>Gets or sets the unique job identifier.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the Jellyfin item ID.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the subtitle stream index.</summary>
    public int SubtitleIndex { get; set; }

    /// <summary>Gets or sets the current status.</summary>
    public SyncJobStatus Status { get; set; } = SyncJobStatus.Queued;

    /// <summary>Gets or sets a progress value from 0.0 to 1.0.</summary>
    public double Progress { get; set; }

    /// <summary>
    /// Gets or sets the current phase label (e.g. "Extracting subtitle", "Syncing", "Replacing").
    /// The frontend displays this to give the user context about what's happening.
    /// </summary>
    public string Phase { get; set; } = "Preparing";

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
    private readonly ILibraryManager _libraryManager;
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
    private readonly ConcurrentDictionary<string, byte> _passInFlight = new(StringComparer.Ordinal);
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
    private static readonly HashSet<string> AllowedVadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "subs", "webrtc", "subs_then_webrtc", "auditok", "subs_then_auditok", "subs_then_silero", "silero"
    };

    /// <summary>Allowed values for the --output-encoding config option.</summary>
    private static readonly HashSet<string> AllowedOutputEncodings = new(StringComparer.OrdinalIgnoreCase)
    {
        "utf-8", "ascii", "latin-1", "utf-8-sig", "utf-16"
    };

    /// <summary>
    /// Initializes a new instance of the <see cref="SubSyncService"/> class.
    /// </summary>
    /// <param name="logger">Logger instance.</param>
    /// <param name="libraryManager">Jellyfin library manager.</param>
    /// <param name="libraryMonitor">Jellyfin library filesystem monitor (for targeted folder rescans).</param>
    public SubSyncService(ILogger<SubSyncService> logger, ILibraryManager libraryManager, ILibraryMonitor libraryMonitor)
    {
        _logger = logger;
        _libraryManager = libraryManager;
        _libraryMonitor = libraryMonitor;

        // Evict completed/failed jobs older than 1 hour, check every 30 minutes
        _cleanupTimer = new Timer(_ => CleanupOldJobs(), null, TimeSpan.FromMinutes(30), TimeSpan.FromMinutes(30));
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
        if (config is not null && !string.IsNullOrWhiteSpace(config.FfmpegPath))
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
    /// <returns>The queued job and the cost of each part.</returns>
    internal (SyncJob Job, string Timing) EnqueueSyncTimed(
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

        // Embedded extraction never trusts Jellyfin's stream numbering: the real
        // container stream index is resolved at run time by probing the file with
        // ffmpeg (see ResolveContainerSubtitleIndexAsync). Kept here only as a
        // display/logging handle on the originally selected stream.
        var subtitleOrdinal = subtitleStream.Index;

        var config = Services.SettingsSource.Current() ?? new Configuration.PluginConfiguration();
        sourcesMs = phase.ElapsedMilliseconds;
        phase.Restart();

        _logger.LogInformation(
            "Queued sync: item {ItemId} subtitle stream {SubtitleIndex} — output mode: {Mode}",
            itemId, subtitleIndex, config.SyncModeCopy ? "copy (.SYNCED.srt)" : "replace original in place");

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
        PluginLog.Info(
            $"queued: job={job.Id} item={itemId} stream={subtitleIndex} mode={job.Mode} "
            + $"batch={batchId ?? "(standalone)"} language={subtitleStream.Language ?? "und"} "
            + $"external={subtitleStream.IsExternal} forced={subtitleStream.IsForced} "
            + $"codec={subtitleStream.Codec} video={video.Path}");

        lock (_queueLock)
        {
            _runOrder.Add(job);
        }

        WakePump();
        logMs = phase.ElapsedMilliseconds;
        total.Stop();

        var timing = $"item={itemMs} ms, sources={sourcesMs} ms, settings={settingsMs} ms, "
            + $"log={logMs} ms, total={total.ElapsedMilliseconds} ms";

        // Anything beyond a few milliseconds here is worth naming: a slow enqueue is invisible in
        // every other view and looks exactly like a scheduler that will not parallelise.
        if (total.ElapsedMilliseconds > 250)
        {
            PluginLog.Info($"enqueue slow: {timing} stream={subtitleIndex} video={video.Path}");
        }

        return (job, timing);
    }

    /// <summary>
    /// Enqueues a batch of tasks as one FIFO unit. Tasks that fail validation
    /// are recorded as failed jobs inside the batch instead of aborting it.
    /// </summary>
    /// <param name="label">Scope label shown in history (e.g. "Series · Season 2").</param>
    /// <param name="tasks">The task list (item, subtitle index, display title).</param>
    /// <param name="mode">Multi-subtitle mode for the whole batch (normal | parallel | fast).</param>
    /// <returns>The created batch jobs (includes pre-failed entries).</returns>
    public IReadOnlyList<SyncJob> CreateBatch(string label, IReadOnlyList<(Guid ItemId, int SubtitleIndex, string? Title)> tasks, string? mode = null)
    {
        var batchId = Guid.NewGuid().ToString("N");
        var resolvedMode = NormalizeMode(mode ?? Services.SettingsSource.Current()?.MultiSyncMode);
        var jobs = new List<SyncJob>(tasks.Count);
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
                jobs.Add(queued.Job);
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

        return jobs;
    }

    /// <summary>
    /// Cancels all queued (not yet started) jobs of a batch. A running job is
    /// allowed to finish.
    /// </summary>
    /// <param name="batchId">The batch identifier.</param>
    public void CancelBatch(string batchId)
    {
        var queuedCancelled = 0;
        lock (_queueLock)
        {
            foreach (var job in _runOrder.Where(j => j.BatchId == batchId && j.Status == SyncJobStatus.Queued))
            {
                job.Status = SyncJobStatus.Cancelled;
                job.FinishedAtUtc = DateTime.UtcNow;
                queuedCancelled++;
                _logger.LogInformation("Cancelled queued job {JobId} of batch {BatchId}", job.Id, batchId);
            }
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

    /// <summary>
    /// Kills everything: queued jobs (any batch) are cancelled and every running job's
    /// ffsubsync/ffmpeg processes are terminated. Backs the UI's Kill action, which is
    /// what the Cancel button turns into while processes are still running.
    /// </summary>
    /// <returns>How many jobs were dropped from the queue and how many runs were killed.</returns>
    public (int QueuedCancelled, int RunningKilled) KillAll()
    {
        var queuedCancelled = 0;
        lock (_queueLock)
        {
            foreach (var job in _runOrder.Where(j => j.Status == SyncJobStatus.Queued))
            {
                job.Status = SyncJobStatus.Cancelled;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.Phase = "Cancelled";
                queuedCancelled++;
            }
        }

        var runningKilled = 0;
        foreach (var kvp in _jobCancellation)
        {
            try
            {
                kvp.Value.Cancel();
                runningKilled++;
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

        var processesKilled = 0;
        foreach (var process in _liveProcesses.Values.ToList())
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    processesKilled++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Could not kill process {Pid}", process.Id);
            }
        }

        // Give the kernel a moment, then report what is left instead of claiming success.
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline && _liveProcesses.Values.Any(p => !SafeHasExited(p)))
        {
            Thread.Sleep(100);
        }

        var survivors = _liveProcesses.Values.Count(p => !SafeHasExited(p));
        _logger.LogInformation(
            "Kill requested: {Queued} queued task(s) cancelled, {Running} run token(s) cancelled, {Killed} process tree(s) killed, {Survivors} still alive",
            queuedCancelled, runningKilled, processesKilled, survivors);

        // The plugin's own log, so a kill is verifiable from the one file that gets handed over for
        // debugging. Which phases the jobs were in matters: a job killed while extracting a subtitle
        // and a job killed while analysing speech fail in the same place otherwise.
        var runningPhases = _jobs.Values
            .Where(j => j.Status == SyncJobStatus.Running)
            .Select(j => $"{j.Id[..8]}={j.Phase} ({j.Label ?? j.BatchLabel ?? j.ItemId.ToString()[..8]})")
            .ToList();
        PluginLog.Info(
            $"KILL requested: {queuedCancelled} queued cancelled, {runningKilled} run token(s), "
            + $"{processesKilled} process tree(s) killed, {survivors} survivor(s)"
            + (runningPhases.Count == 0 ? string.Empty : " \u00b7 still reporting: " + string.Join(", ", runningPhases)));

        return (queuedCancelled, processesKilled > 0 ? Math.Max(processesKilled, runningKilled) : runningKilled);
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
        /// built (embedded extraction or an uncached audio pass). Used only to decide whether
        /// a second job may join the wave for the same file — storage scheduling itself is
        /// left to the OS, which sees the real device queue.
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
        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            var name = Path.GetFileName(directory);

            // The reference tree has its own owner and its own lifetime.
            if (name.Equals("ref", StringComparison.OrdinalIgnoreCase) || _jobs.ContainsKey(name))
            {
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

        return removed;
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

    /// <summary>Wakes the extraction lane, starting it if it is not running.</summary>
    private void WakeExtractor()
    {
        var wanted = Math.Clamp(
            (Plugin.Instance?.Configuration?.ParallelWorkers ?? DefaultParallelWorkers) / 2,
            1,
            MaxExtractionLanes);

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
    private const int MaxExtractionLanes = 3;

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
            if (!_passInFlight.TryAdd(videoPath, 0))
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
                (ctx.Config.VadMethod ?? "subs_then_webrtc") + "|audio",
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

            usedVolumes.Add(volume);
            taken.Add(candidate.Id);
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

            wave.Add(candidate);
        }

        return wave.OrderBy(j => j.BatchIndex).ToList();
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
        Func<SyncJob, bool>? mayStart = null)
    {
        var slots = limit - running.Count;
        if (slots <= 0)
        {
            return new List<SyncJob>();
        }

        var runningVolumes = running.Select(volumeOf).ToList();
        var runningItems = running.Select(j => j.ItemId).ToList();

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
                MayStart = mayStart
            });
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
    private async Task PumpAsync()
    {
        var inFlight = new Dictionary<Task, SyncJob>();

        while (!_disposing)
        {
            List<SyncJob> toStart;
            var limit = 1;
            var running = 0;
            List<(string? VideoPath, bool StillNeeded)> finishedVideos;

            LogWorkerLimit();

            lock (_queueLock)
            {
                finishedVideos = new List<(string?, bool)>();

                foreach (var finished in inFlight.Where(kvp => kvp.Key.IsCompleted).Select(kvp => kvp.Key).ToList())
                {
                    var finishedJob = inFlight[finished];
                    inFlight.Remove(finished);

                    var finishedPath = _jobContexts.TryGetValue(finishedJob.Id, out var finishedContext)
                        ? finishedContext.Video.Path
                        : null;

                    // A reference is only worth keeping while another subtitle of that same media file
                    // is still going to use it — queued *or* already running. Counting only the queued
                    // jobs deleted the tree out from under the running ones: the last queued task of a
                    // batch is dispatched while up to `ParallelWorkers` jobs of that very file sit in
                    // ffsubsync reading the reference it just removed, and the losers of that race
                    // either failed ("unable to read reference") or were handed the whole container to
                    // demux instead. Once no job of the file is queued or running, this run's copy
                    // goes: a wrong reference must not be able to poison a later run of the file.
                    var stillNeeded = finishedPath is not null
                        && _runOrder.Any(j => (j.Status == SyncJobStatus.Queued
                                || j.Status == SyncJobStatus.Running)
                            && _jobContexts.TryGetValue(j.Id, out var queuedContext)
                            && string.Equals(queuedContext.Video.Path, finishedPath, StringComparison.Ordinal));

                    finishedVideos.Add((finishedPath, stillNeeded));
                }

                running = inFlight.Count;
                var head = _runOrder.FirstOrDefault(j => j.Status == SyncJobStatus.Queued);

                if (head is null)
                {
                    toStart = new List<SyncJob>();
                }
                else
                {
                    var config = Services.SettingsSource.Current();
                    var headMode = ResolveModeForBatch(head);
                    limit = IsParallelMode(headMode)
                        ? NormalizeWorkers(config?.ParallelWorkers ?? DefaultParallelWorkers)
                        : 1;

                    toStart = PlanStart(
                        _runOrder.Where(j => j.Status == SyncJobStatus.Queued),
                        inFlight.Values.ToList(),
                        headMode,
                        head.BatchId,
                        limit,
                        job => MediaVolume.Of(_jobContexts.TryGetValue(job.Id, out var vc) ? vc.Video.Path : null),
                        job => JobNeedsHeavyIo(job, headMode),
                        // Only start a job whose subtitle is already out of the file. The old gate
                        // ("may this job share the file with a running one?") still let jobs through
                        // once a reference existed, and each of them then ran its own pass: measured on
                        // a real episode, the lane produced six subtitles in one pass of 448 MB and the
                        // six jobs then ran four more passes of ~350 MB each, 2 GB of reading for
                        // nothing. The lane runs extraction; a job that is not ready waits in the queue
                        // instead of duplicating the work. The one exception is a lane that is not
                        // running at all (disposed, or died), where jobs must be able to extract for
                        // themselves rather than never start.
                        // canShareMediaFile: a second job may join a file whose subtitle is already out.
                        job => ExtractionReady(job),
                        // MayStart: the same, plus the escape hatch for a lane that is gone.
                        job => ExtractionReady(job)
                            || (!LaneAlive
                                && DateTime.UtcNow - _lastPassFinishedUtc > TimeSpan.FromSeconds(20)));
                }
            }

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

            if (toStart.Count == 0)
            {
                if (_disposing)
                {
                    break;
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
                continue;
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
            job.Status = SyncJobStatus.Cancelled;
            job.Phase = "Cancelled";
            job.Error = "Cancelled by user.";
            _logger.LogInformation("Job {JobId} cancelled by user", job.Id);
        }
        finally
        {
            _jobCancellation.TryRemove(job.Id, out _);
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
        var batchJobs = CreateBatch(batchLabel, tasks);
        var batchId = batchJobs.FirstOrDefault()?.BatchId;
        if (string.IsNullOrEmpty(batchId))
        {
            result.FailedOrCancelled = tasks.Count;
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
        var jellyfinEmbedded = (mediaSources.Count > 0 ? mediaSources[0] : null)?.MediaStreams
            .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && !s.IsExternal)
            .OrderBy(s => s.Index)
            .ToList() ?? new List<MediaBrowser.Model.Entities.MediaStream>();

        var pos = jellyfinEmbedded.FindIndex(s => s.Index == target.Index);
        if (pos < 0 && jellyfinEmbedded.Count == 1)
        {
            pos = 0; // single embedded track — safe positional fallback
        }

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
        if (!isEmbedded)
        {
            // External sidecar: the file's embedded text track is a legitimate reference.
            return null;
        }

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
    /// <param name="ShiftMs">Applied offset in milliseconds (signed; + = subtitles moved later).</param>
    /// <param name="Ratio">Fitted time ratio; 1.0 when no framerate correction was needed.</param>
    /// <param name="DriftMs">Cumulative drift the ratio fixes over the subtitle's runtime.</param>
    internal readonly record struct SyncChange(long ShiftMs, double Ratio, long DriftMs)
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
        return new SyncChange(shiftMs, ratio, driftMs);
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
        Directory.CreateDirectory(tempDir);

        // The file is checked here instead of when the task is queued: queueing must not touch the
        // media share (a stat per task slowed a 50-task batch to a minute while a job was reading the
        // same share, which starved the scheduler). One stat per job, at the point where it matters.
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException($"The video file is no longer on disk: {videoPath}");
        }

        // Stream of the media file ffsubsync should take its speech signal from. Embedded
        // inputs set this so the subtitle being fixed is not used as its own reference.
        string? referenceStream = null;

        // True when this job is aligned against a subtitle taken from a sibling track instead of
        // against the audio. Such a result is only ever as good as that track, so it is checked
        // before anything is written.
        var usedSubtitleReference = false;

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
            // Step 0: Ensure ffsubsync is available
            job.Phase = "Preparing";
            job.Progress = 0.0;

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
                subtitleInputPath = Path.Combine(tempDir, $"subtitle_{job.SubtitleIndex}.srt");

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
                job.ExtractionNote = extractionMethod == "ffmpeg"
                    ? $"demuxed with ffmpeg, {extractionWatch.ElapsedMilliseconds} ms"
                    : extractionMethod == "matroska-cached"
                        ? "reused from the pass that read this file for another subtitle"
                        : $"read through the container index ({extractionMethod}), {extractionWatch.ElapsedMilliseconds} ms";

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
                speechKey = SpeechCache.KeyFor(
                    videoPath,
                    (config.VadMethod ?? "subs_then_webrtc") + "|audio",
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

                serializeSpeech = true;
                job.Phase = SyncPhaseLabel(fromCache: false, audioReference: true);
                PluginLog.Info($"[{job.Id}] reference: method=audio why={why}");
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

            var args = BuildFfSubSyncArgs(config, referencePath, subtitleInputPath, tempOutput, tempDir, serializeSpeech, referenceStream);

            _logger.LogInformation("Running ffsubsync ({Exe}): {Args}", ffsubsyncExe, args);
            PluginLog.Info($"[{job.Id}] ffsubsync start: exe={ffsubsyncExe} cachedSpeech={usingCachedSpeech} reference={referenceStream ?? "(default)"} args={string.Join(' ', args)}");

            // Parse ffsubsync stderr in real-time for progress updates.
            // tqdm format: " 42%|████▎     | 3000.0/6997.696 [00:27<00:34, 115.36it/s]"
            // Phase messages: "extracting speech...", "computing alignments...", "writing output..."
            var engineWatch = System.Diagnostics.Stopwatch.StartNew();
            // Keep the tail of stderr: "ffsubsync exited with code 1" on its own tells nobody anything,
            // and the reason (an unreadable reference, a subtitle with no text, a demux error) is
            // always in the last few lines ffsubsync printed.
            var engineErrors = new List<string>();
            var exitCode = await RunProcessWithStderrCallbackAsync(
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
                cancellationToken).ConfigureAwait(false);
            engineWatch.Stop();
            PluginLog.Info($"[{job.Id}] ffsubsync exit={exitCode} after {engineWatch.ElapsedMilliseconds} ms");

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
                args = BuildFfSubSyncArgs(config, referencePath, subtitleInputPath, tempOutput, tempDir, serializeSpeech, referenceStream);
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
                    cancellationToken).ConfigureAwait(false);
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
                return;
            }

            _logger.LogInformation("ffsubsync produced synced subtitle ({Size} bytes)", new FileInfo(tempOutput).Length);

            // ffsubsync writes an output file even when the timings come out identical, which produced
            // a ".SYNCED" sidecar with the same timing as its source: no benefit, one more subtitle
            // track in the library. Measured before anything is written, so nothing is touched.
            var measured = MeasureSyncChange(subtitleInputPath, tempOutput);

            // Nothing destructive is ever written: a measured rescale that was not asked for (or that
            // is not a real framerate pair) means the engine moved the timeline, and the source
            // subtitle stays untouched while the job says exactly why.
            if (measured is { } scaled
                && !IsRescaleAcceptable(scaled.Ratio, scaled.ShiftMs, config.MaxOffsetSeconds, config.FixFramerate))
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

            // A shift that came from a subtitle reference is only ever as good as that track: a
            // reference taken from a different cut drags every subtitle of the file onto it, and the
            // file that comes out looks exactly like an ordinary success. AGENTS.md has documented
            // MaxSubtitleReferenceOffsetSeconds as a refusal since the reference path was added, so
            // this is that refusal — with the measured numbers, and without touching anything.
            var referenceCeilingMs = Math.Max(1.0, config.MaxSubtitleReferenceOffsetSeconds) * 1000.0;
            if (usedSubtitleReference
                && measured is { } fromReference
                && Math.Abs(fromReference.ShiftMs) > referenceCeilingMs)
            {
                var detail = $"aligned to the reference subtitle {referenceSpec} at {fromReference.ShiftMs} ms";
                _logger.LogWarning(
                    "Sync job {JobId}: refusing a reference-derived shift ({Detail}) — nothing written",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id} REFUSED: {detail}, over the {referenceCeilingMs / 1000.0:0.#} s limit for a "
                    + $"subtitle reference — the reference track is probably not the same cut; nothing written, "
                    + $"source untouched, file={video.Path}");
                job.Status = SyncJobStatus.Failed;
                job.Phase = "Refused";
                job.Error = $"refused: the alignment came from the reference subtitle {referenceSpec} and moved this "
                    + $"subtitle by {fromReference.ShiftMs} ms, more than the {referenceCeilingMs / 1000.0:0.#} s a "
                    + "subtitle reference is trusted for — a shift this size usually means that track is from a "
                    + "different cut. Nothing was written. Raise \"Maximum shift from a subtitle reference\" if the "
                    + "track really is the same cut, or sync this subtitle against the audio instead.";
                job.Progress = 1.0;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.OutputPath = null;
                SafeDelete(tempOutput);
                return;
            }

            // The engine clamps the shift at the configured ceiling, so a result that sits exactly there
            // is the most it was allowed to apply, not what the file needed: writing it would present a
            // guess as a synced subtitle. AGENTS.md documents this as a refusal as well.
            var ceilingMs = config.MaxOffsetSeconds * 1000.0;
            if (measured is { } onCeiling && Math.Abs(onCeiling.ShiftMs) >= ceilingMs - 500)
            {
                var detail = $"the measured offset {onCeiling.ShiftMs} ms sits on the configured ceiling "
                    + $"({config.MaxOffsetSeconds} s)";
                _logger.LogWarning(
                    "Sync job {JobId}: refusing a result pinned to the offset ceiling ({Detail}) — nothing written",
                    job.Id,
                    detail);
                PluginLog.Info(
                    $"job {job.Id} REFUSED: {detail}, so the shift is what the engine was allowed to apply, "
                    + $"not what the file needs; nothing written, source untouched, file={video.Path}");
                job.Status = SyncJobStatus.Failed;
                job.Phase = "Refused";
                job.Error = $"refused: the engine clamped the shift at the {config.MaxOffsetSeconds} s ceiling "
                    + $"({detail}), so this subtitle is further out than the plugin was allowed to move it. Nothing "
                    + "was written. Raise \"Maximum offset\" and run it again if the file really is that far out.";
                job.Progress = 1.0;
                job.FinishedAtUtc = DateTime.UtcNow;
                job.OutputPath = null;
                SafeDelete(tempOutput);
                return;
            }

            if (usedSubtitleReference
                && measured is { } fromReferenceNote
                && Math.Abs(fromReferenceNote.ShiftMs) > 10000)
            {
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
            }

            if (measured is { IsNoChange: true } noChange)
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

            PluginLog.Info(
                $"job {job.Id} completed: mode={job.Mode} output={job.OutputPath ?? "(none)"} bytes={outputSize?.ToString() ?? "unknown"} change={job.Outcome ?? "unknown"} extraction={job.ExtractionNote ?? "n/a"}");

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

        // A pass in flight is reading the media share; shutdown must not wait for it.
        try { _laneStop.Cancel(); }
        catch (ObjectDisposedException) { /* already cancelled */ }

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
    /// Evicts completed and failed jobs from the in-memory store to prevent memory leaks.
    /// </summary>
    private void CleanupOldJobs()
    {
        try
        {
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

            var cutoff = DateTime.UtcNow.AddHours(-1);
            var toRemove = _jobs
                .Where(kvp => kvp.Value.Status is SyncJobStatus.Completed or SyncJobStatus.Failed or SyncJobStatus.Cancelled)
                .Where(kvp => kvp.Value.Status != SyncJobStatus.Completed ||
                              (kvp.Value.FinishedAtUtc is not null && kvp.Value.FinishedAtUtc < cutoff) ||
                              (_jobs.Count > 50))
                .Select(kvp => kvp.Key)
                .ToList();

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
        string? referenceStream = null)
    {
        // Validate config values to prevent argument injection
        var vadMethod = AllowedVadMethods.Contains(config.VadMethod)
            ? config.VadMethod
            : "subs_then_webrtc";
        var outputEncoding = AllowedOutputEncodings.Contains(config.OutputEncoding)
            ? config.OutputEncoding
            : "utf-8";

        // ArgumentList passes argv directly — no string-quoting layer, so paths
        // with spaces/unicode can never split into extra arguments.
        var args = new List<string>
        {
            videoPath,
            "-i", subtitleInput,
            "-o", subtitleOutput,
            "--max-offset-seconds", config.MaxOffsetSeconds.ToString(CultureInfo.InvariantCulture),
            "--max-subtitle-seconds", config.MaxSubtitleSeconds.ToString(CultureInfo.InvariantCulture),
            "--vad", vadMethod,
            "--output-encoding", outputEncoding,
            "--ffmpeg-path", ResolveFfmpegPath()
        };

        foreach (var flag in FramerateArgs(config.FixFramerate, config.UseGoldenSectionSearch))
        {
            args.Add(flag);
        }

        if (!string.IsNullOrWhiteSpace(referenceStream))
        {
            args.Add("--reference-stream");
            args.Add(referenceStream);
        }

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
                    + $"blockOffsets={manyStats.BlockOffsets} ok={manyOk} reason={manyReason} file={videoPath}");

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
    private async Task ExtractSubtitleWithProgressAsync(
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

        var lastReported = -1.0;
        var exitCode = await RunProcessWithStderrCallbackAsync(
            ffmpegPath,
            args,
            null,
            line =>
            {
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
            cancellationToken).ConfigureAwait(false);

        if (exitCode != 0 && !File.Exists(outputPath))
        {
            throw new InvalidOperationException($"ffmpeg subtitle extraction failed with exit code {exitCode}.");
        }
    }

    /// <summary>
    /// Reads the processed timestamp from one line of ffmpeg's <c>-progress</c> output
    /// (<c>out_time_us=…</c>), falling back to the human-readable <c>out_time=HH:MM:SS</c>.
    /// </summary>
    /// <param name="line">One progress line.</param>
    /// <returns>Seconds processed, or -1 when the line is not a progress line.</returns>
    private static double ParseFfmpegProgressSeconds(string line)
    {
        var trimmed = line.Trim();

        if (trimmed.StartsWith("out_time_us=", StringComparison.Ordinal)
            && long.TryParse(trimmed[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var micros))
        {
            return micros / 1_000_000.0;
        }

        if (trimmed.StartsWith("out_time_ms=", StringComparison.Ordinal)
            && long.TryParse(trimmed[12..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var millis))
        {
            return millis / 1000.0;
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
        _liveProcesses[process.Id] = process;

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
        _liveProcesses[process.Id] = process;

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
        _liveProcesses[process.Id] = process;

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
    private async Task<int> RunProcessWithStderrCallbackAsync(
        string executable, IReadOnlyList<string> arguments, string? workingDir,
        Action<string>? onStderrLine, CancellationToken cancellationToken)
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
        _liveProcesses[process.Id] = process;

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

        var stdout = await process.StandardOutput.ReadToEndAsync().ConfigureAwait(false);
        var stderr = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);

        await process.WaitForExitAsync().ConfigureAwait(false);

        var output = string.Concat(stdout, stderr).Trim();
        return (process.ExitCode, output);
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
