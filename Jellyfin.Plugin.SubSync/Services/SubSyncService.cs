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

    /// <summary>Gets or sets the runtime identifier the bundled binary was built for, if present.</summary>
    public string? BundledRid { get; set; }
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
    private readonly SemaphoreSlim _wakePump = new(0, 1);
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
        var config = Plugin.Instance?.Configuration;

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
        var config = Plugin.Instance?.Configuration;
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
            IsInstalled = File.Exists(ManagedFfSubSyncPath)
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
        status.SpeechCacheSummary = SpeechCache.Describe();
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
    /// Lists subtitle streams for a given video item.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <returns>A list of subtitle infos, or null if the item is not found.</returns>
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
        var languageFilter = Plugin.Instance?.Configuration?.SyncLanguages ?? Array.Empty<string>();

        // Image-based tracks (PGS, VobSub, DVB, XSUB) can never be aligned — they are
        // left out entirely so they cannot be picked and fail. Tracks outside the
        // configured language filter are hidden too, so the UI only ever offers work
        // that can actually succeed.
        return source.MediaStreams
            .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle)
            .Where(s => !LanguageSupport.IsImageBased(s.Codec))
            .Where(s => LanguageSupport.MatchesFilter(s.Language, languageFilter))
            .Select(s =>
            {
                return new SubtitleInfo
                {
                    Index = s.Index,
                    Title = s.DisplayTitle ?? s.Language ?? $"Track {s.Index}",
                    Language = s.Language ?? "und",
                    IsExternal = s.IsExternal,
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
        if (subtitleIndex < 0)
        {
            throw new ArgumentException("Subtitle index must be non-negative.");
        }

        var item = _libraryManager.GetItemById(itemId);
        if (item is not Video video)
        {
            throw new InvalidOperationException($"Item {itemId} is not a video.");
        }

        if (!File.Exists(video.Path))
        {
            throw new FileNotFoundException($"Video file not found.");
        }

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

        var config = Plugin.Instance?.Configuration ?? new Configuration.PluginConfiguration();

        _logger.LogInformation(
            "Queued sync: item {ItemId} subtitle stream {SubtitleIndex} — output mode: {Mode}",
            itemId, subtitleIndex, config.SyncModeCopy ? "copy (-SYNCED.srt)" : "replace original in place");

        var job = new SyncJob
        {
            ItemId = itemId,
            SubtitleIndex = subtitleIndex,
            BatchId = batchId,
            BatchLabel = batchLabel,
            BatchIndex = batchIndex,
            Label = label,
            Mode = NormalizeMode(mode ?? Plugin.Instance?.Configuration?.MultiSyncMode)
        };

        _jobs[job.Id] = job;
        _jobContexts[job.Id] = (video, subtitleStream, subtitleOrdinal, config);

        lock (_queueLock)
        {
            _runOrder.Add(job);
        }

        WakePump();
        return job;
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
        var resolvedMode = NormalizeMode(mode ?? Plugin.Instance?.Configuration?.MultiSyncMode);
        var jobs = new List<SyncJob>(tasks.Count);

        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            try
            {
                jobs.Add(EnqueueSync(task.ItemId, task.SubtitleIndex, task.Title, batchId, label, i, resolvedMode));
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
        }

        return jobs;
    }

    /// <summary>
    /// Cancels all queued (not yet started) jobs of a batch. A running job is
    /// allowed to finish.
    /// </summary>
    /// <param name="batchId">The batch identifier.</param>
    public void CancelBatch(string batchId)
    {
        lock (_queueLock)
        {
            foreach (var job in _runOrder.Where(j => j.BatchId == batchId && j.Status == SyncJobStatus.Queued))
            {
                job.Status = SyncJobStatus.Cancelled;
                job.FinishedAtUtc = DateTime.UtcNow;
                _logger.LogInformation("Cancelled queued job {JobId} of batch {BatchId}", job.Id, batchId);
            }
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
    private bool SpeechIsCached(SyncJob job)
    {
        try
        {
            if (!_jobContexts.TryGetValue(job.Id, out var ctx))
            {
                return false;
            }

            var key = SpeechCache.KeyFor(
                ctx.Video.Path,
                (ctx.Config.VadMethod ?? "subs_then_webrtc") + "|audio",
                EngineIdentity());
            return SpeechCache.TryGet(key) is not null;
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
            if (candidates.Count >= policy.Limit * 4)
            {
                break; // enough to fill a wave several times over; keeps the scan bounded
            }
        }

        var wave = new List<SyncJob>();
        var taken = new HashSet<string>(StringComparer.Ordinal);
        var claimedItems = new HashSet<Guid>();
        var usedVolumes = new HashSet<string>(StringComparer.Ordinal);

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
        var heavy = policy.IsHeavyIo?.Invoke(candidate) ?? false;

        if (!claimedItems.Add(candidate.ItemId))
        {
            if (heavy || policy.CanShareMediaFile?.Invoke(candidate) != true)
            {
                return false;
            }
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

    /// <summary>Worker count for parallel mode, clamped to a sane range.</summary>
    private static int NormalizeWorkers(int workers) => workers < 1 ? 1 : (workers > 8 ? 8 : workers);

    /// <summary>Default worker count for parallel mode.</summary>
    public const int DefaultParallelWorkers = 4;

    private void WakePump()
    {
        lock (_queueLock)
        {
            if (_pumpTask is null || _pumpTask.IsCompleted)
            {
                _pumpTask = Task.Run(PumpAsync);
            }
        }

        try { _wakePump.Release(); } catch (SemaphoreFullException) { /* already signalled */ }
    }

    private async Task PumpAsync()
    {
        while (!_disposing)
        {
            List<SyncJob> jobs;
            lock (_queueLock)
            {
                // The head of the queue decides how much runs at once: normal/fast run
                // one job at a time, parallel runs up to ParallelWorkers jobs of the same
                // batch together. Batches never interleave, so FIFO order still holds.
                var head = _runOrder.FirstOrDefault(j => j.Status == SyncJobStatus.Queued);
                if (head is null)
                {
                    jobs = new List<SyncJob>();
                }
                else
                {
                    var config = Plugin.Instance?.Configuration;
                    var headMode = ResolveModeForBatch(head);
                    var limit = IsParallelMode(headMode)
                        ? NormalizeWorkers(config?.ParallelWorkers ?? DefaultParallelWorkers)
                        : 1;

                    jobs = SelectWave(
                        _runOrder.Where(j => j.Status == SyncJobStatus.Queued),
                        headMode,
                        head.BatchId,
                        new WavePolicy
                        {
                            Limit = limit,
                            VolumeOf = job => MediaVolume.Of(_jobContexts.TryGetValue(job.Id, out var vc) ? vc.Video.Path : null),
                            IsHeavyIo = job => JobNeedsHeavyIo(job, headMode),
                            CanShareMediaFile = job => SpeechIsCached(job)
                        });
                }
            }

            if (jobs.Count == 0)
            {
                if (_disposing)
                {
                    break;
                }

                await _wakePump.WaitAsync().ConfigureAwait(false);
                continue;
            }

            var runOne = async Task (SyncJob job) =>
            {
                try
                {
                    await RunSyncJobWithContext(job).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Unhandled exception in sync job {JobId}", job.Id);
                    job.Status = SyncJobStatus.Failed;
                    job.Error = $"Internal error: {ex.Message}";
                    RecordSweepOutcome(job, ok: false, outputPath: null, error: ex.Message);
                }
                finally
                {
                    job.FinishedAtUtc = DateTime.UtcNow;
                }
            };

            if (jobs.Count == 1)
            {
                _logger.LogInformation(
                    "Pump starting job {JobId} (batch {Batch}, mode {Mode})",
                    jobs[0].Id, jobs[0].BatchId ?? "none", NormalizeMode(jobs[0].Mode));
                await runOne(jobs[0]).ConfigureAwait(false);
                continue;
            }

            _logger.LogInformation(
                "Pump starting {Count} jobs in parallel (batch {Batch}, mode {Mode})",
                jobs.Count, jobs[0].BatchId ?? "none", NormalizeMode(jobs[0].Mode));
            await Task.WhenAll(jobs.Select(runOne)).ConfigureAwait(false);
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
        var config = Plugin.Instance?.Configuration;
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
    /// <returns>An ffmpeg stream specifier ("s:1", "a:0") or null to leave the default.</returns>
    public static string? SelectReferenceStream(bool isEmbedded, IReadOnlyList<string> subtitleCodecs, int targetOrdinal)
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
                    starts.Add((h * 3600.0) + (m * 60.0) + s);
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
    /// Describes what a successful sync actually changed, by comparing cue
    /// timings of the original and the synced file: the applied offset in
    /// milliseconds (signed; + = subtitles moved later) and, when ffsubsync
    /// corrected a framerate mismatch, the fitted time ratio plus the total
    /// cumulative drift it fixed over the subtitle's runtime.
    /// </summary>
    internal static string? DescribeSyncChange(string inputPath, string outputPath)
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

        if (Math.Abs(ratio - 1.0) < 1e-4)
        {
            return $"{shiftMs:+0;-0} ms offset";
        }

        var driftMs = (long)Math.Round((ratio - 1.0) * before[^1] * 1000.0);
        return $"{shiftMs:+0;-0} ms offset at start \u00b7 framerate ratio {ratio:0.0000}\u00d7 (\u2248{driftMs:+0;-0} ms cumulative drift)";
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
        Directory.CreateDirectory(tempDir);

        // Stream of the media file ffsubsync should take its speech signal from. Embedded
        // inputs set this so the subtitle being fixed is not used as its own reference.
        string? referenceStream = null;

        // Paths for the safe atomic-replace workflow
        string? backupPath = null;   // .bak of original subtitle file (replace mode only)
        string? tempOutput = null;   // ffsubsync output in temp dir
        string? changedDir = null;   // folder touched by this job (for the targeted library rescan)

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
            var allowedLanguages = Plugin.Instance?.Configuration?.SyncLanguages ?? Array.Empty<string>();
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
                referenceStream = SelectReferenceStream(true, subtitleCodecs, subtitleStreamOrdinal);
                _logger.LogInformation(
                    "Embedded sync of {Video}: deriving the speech signal from '{Reference}'",
                    videoPath, referenceStream);

                var extractionMethod = await ExtractEmbeddedAsync(
                    videoPath,
                    subtitleStreamOrdinal,
                    containerIndex,
                    subtitleInputPath,
                    config,
                    job,
                    video.RunTimeTicks,
                    cancellationToken).ConfigureAwait(false);

                // A kill during extraction must not turn into "try the next method".
                cancellationToken.ThrowIfCancellationRequested();
                job.Phase = "Extracted subtitle with " + DescribeExtraction(extractionMethod);
            }

            // Step 2: Run ffsubsync → temp output
            job.Phase = "Analyzing speech";
            job.Progress = 0.1;

            tempOutput = Path.Combine(tempDir, "synced.srt");

            // "fast" mode: the speech analysis depends only on the media file, the VAD
            // method and the ffsubsync build — not on which subtitle is being synced —
            // so it is computed once and reused for the other subtitles of that file.
            var mode = NormalizeMode(job.Mode);
            var referencePath = videoPath;
            var serializeSpeech = false;
            string? speechKey = null;
            var usingCachedSpeech = false;

            // The speech signal depends on the media file, the VAD method and the engine
            // build — never on the subtitle, its language or the mode. Analysing it is work
            // that happens anyway, so it is always kept: the other subtitles of that file and
            // any later run then skip the audio pass entirely.
            var usesAudioReference = referenceStream is null
                || referenceStream.StartsWith("a:", StringComparison.Ordinal);
            if (usesAudioReference)
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
                    referencePath = cached;
                    usingCachedSpeech = true;
                    job.Phase = "Syncing (reusing the audio analysis)";
                    _logger.LogInformation("Reusing the stored audio analysis for {Video}", videoPath);
                }
                else
                {
                    referencePath = SpeechCache.CreateReferenceLink(videoPath, speechKey);
                    serializeSpeech = true;
                    job.Phase = "Syncing (analysing the audio)";
                }
            }

            var args = BuildFfSubSyncArgs(config, referencePath, subtitleInputPath, tempOutput, tempDir, serializeSpeech, referenceStream);

            _logger.LogInformation("Running ffsubsync ({Exe}): {Args}", ffsubsyncExe, args);

            // Parse ffsubsync stderr in real-time for progress updates.
            // tqdm format: " 42%|████▎     | 3000.0/6997.696 [00:27<00:34, 115.36it/s]"
            // Phase messages: "extracting speech...", "computing alignments...", "writing output..."
            var exitCode = await RunProcessWithStderrCallbackAsync(
                ffsubsyncExe, args, tempDir,
                line =>
                {
                    ParseFfSubSyncStderr(line, job);
                },
                cancellationToken).ConfigureAwait(false);

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
                exitCode = await RunProcessWithStderrCallbackAsync(
                    ffsubsyncExe, args, tempDir,
                    line => ParseFfSubSyncStderr(line, job),
                    cancellationToken).ConfigureAwait(false);
            }

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"ffsubsync exited with code {exitCode}.");
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
                job.Outcome = "already in sync (shift under 3 s) \u2014 no change needed";
                job.Phase = "Complete";
                job.Status = SyncJobStatus.Completed;
                job.Progress = 1.0;
                _logger.LogInformation("Sync job {JobId}: subtitle already in sync \u2014 no output written", job.Id);
                return;
            }

            _logger.LogInformation("ffsubsync produced synced subtitle ({Size} bytes)", new FileInfo(tempOutput).Length);

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
                    // Jellyfin parses sidecar filenames from the END looking for a
                    // language token. Pure-language files ("uzb.srt") parse clean:
                    // language "Uzbek", no title. "uzb-SYNCED.srt" breaks the parse
                    // ("Undefined" + raw stem as title), so for those files the copy
                    // is named "{lang}.SYNCED.srt" which parses exactly like the
                    // original. All other stems keep the collision-safe old name.
                    var lang = string.IsNullOrWhiteSpace(subtitleStream.Language)
                        ? null
                        : subtitleStream.Language.Trim().ToLowerInvariant();
                    var target = lang is not null && string.Equals(stem, lang, StringComparison.OrdinalIgnoreCase)
                        ? Path.Combine(dir, $"{lang}.SYNCED.srt")
                        : Path.Combine(dir, stem + "-SYNCED.srt");

                    File.Copy(tempOutput, target, overwrite: true);
                    job.OutputPath = target;
                    changedDir = dir;
                    _logger.LogInformation("Synced copy written: {Original} → {Target} (stem={Stem}, lang={Lang}, original untouched)", original, target, stem, lang ?? "(none)");
                }
                else
                {
                    job.Phase = "Replacing subtitle";
                    job.Progress = 0.85;

                    await ReplaceExternalSubtitle(subtitleStream.Path, tempOutput).ConfigureAwait(false);
                    backupPath = subtitleStream.Path + ".bak.subsync";
                    changedDir = Path.GetDirectoryName(subtitleStream.Path) ?? ".";

                    job.OutputPath = subtitleStream.Path;
                    _logger.LogInformation("Replaced external subtitle: {Path} (backup at {Backup})", subtitleStream.Path, backupPath);
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
                var target = lang is not null
                    ? Path.Combine(videoDir, $"{videoNameNoExt}-SYNCED.{lang}.srt")
                    : Path.Combine(videoDir, $"{videoNameNoExt}-SYNCED.srt");

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
                throw new InvalidOperationException("Subtitle verification failed — synced output is missing or empty.");
            }

            // Step 5: Success — describe what changed (offset ms / framerate),
            // then remove the replace-mode backup.
            var outcomeInput = backupPath ?? (subtitleStream.IsExternal ? subtitleStream.Path : subtitleInputPath);
            if (job.OutputPath is not null)
            {
                job.Outcome = DescribeSyncChange(outcomeInput, job.OutputPath);
            }

            SafeDelete(backupPath);
            backupPath = null;

            job.Phase = "Complete";
            job.Status = SyncJobStatus.Completed;
            job.Progress = 1.0;

            _logger.LogInformation(
                "Sync job {JobId} completed \u2014 wrote: {Output} ({Outcome})",
                job.Id, job.OutputPath ?? "(no output path set)", job.Outcome ?? "unknown");

            // Targeted Jellyfin rescan: tell the library monitor the folder
            // changed. This rescans ONE folder (no full library scan) and makes
            // a newly written sidecar appear in the player.
            if (changedDir is not null)
            {
                _logger.LogInformation("Reporting file change to Jellyfin library monitor for: {Dir}", changedDir);
                _libraryMonitor.ReportFileSystemChanged(changedDir);
            }

            // Also refresh the video item itself so its stream list is re-read.
            await _libraryManager.UpdateItemAsync(
                video,
                video.GetParent(),
                ItemUpdateType.MetadataImport,
                CancellationToken.None).ConfigureAwait(false);
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
    private async Task ReplaceExternalSubtitle(string originalPath, string syncedTempPath)
    {
        if (!File.Exists(originalPath))
        {
            throw new FileNotFoundException($"Original subtitle file not found: {originalPath}");
        }

        var backupPath = originalPath + ".bak.subsync";

        // 1. Copy original → backup (preserves original permissions/attrs)
        _logger.LogInformation("Backing up original subtitle: {Original} → {Backup}", originalPath, backupPath);
        File.Copy(originalPath, backupPath, overwrite: false);

        // 2. Copy synced temp → original (use Copy+Delete instead of cross-device Rename)
        _logger.LogInformation("Replacing subtitle with synced version: {Temp} → {Original}", syncedTempPath, originalPath);
        await Task.Run(() => File.Copy(syncedTempPath, originalPath, overwrite: true)).ConfigureAwait(false);
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
        try { _cleanupTimer.Dispose(); }
        catch { /* already disposed */ }

        // Unblock a parked pump so it can observe _disposing and exit, then
        // release the semaphore. Registered as a DI singleton, Jellyfin calls
        // this once at shutdown.
        try { _wakePump.Release(); }
        catch { /* pump not parked or already released */ }

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

        if (config.UseGoldenSectionSearch)
        {
            args.Add("--gss");
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

        if (config.FastIndexedExtraction && MkvSubtitleExtractor.LooksLikeMatroska(videoPath))
        {
            job.Phase = "Extracting subtitle from the Matroska index";
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var progress = new Action<string>(line => job.Phase = "Extracting subtitle: " + line);
            if (MkvSubtitleExtractor.TryExtract(videoPath, subtitleOrdinal, out var srt, out var why, progress, out var stats, cancellationToken))
            {
                await File.WriteAllTextAsync(outputPath, srt, utf8, cancellationToken).ConfigureAwait(false);
                _logger.LogInformation(
                    "Extracted embedded subtitle in {Ms} ms ({Cues} cues, {Stats}) from {Video}",
                    watch.ElapsedMilliseconds, SrtWriter.CountCues(srt), stats, videoPath);
                return stats.Method;
            }

            skipped.Add("matroska-index: " + why + " [" + stats + "]");
        }

        if (config.FastIndexedExtraction && Mp4SubtitleExtractor.LooksLikeMp4(videoPath))
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

        if (!config.FastIndexedExtraction)
        {
            skipped.Add("indexed extraction is switched off in the settings");
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
        "cue-index" => "from the Matroska cue index",
        "metadata-scan" => "by scanning Matroska metadata only (no full read)",
        "matroska-cues" => "from the Matroska index",
        "mp4-sample-table" => "with the MP4 sample table",
        _ => "with ffmpeg (whole-file read)"
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
            while (!reader.EndOfStream)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is not null && onStderrLine is not null)
                {
                    onStderrLine(line);
                }
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
