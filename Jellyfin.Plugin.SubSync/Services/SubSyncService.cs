using System.Collections.Concurrent;
using System.Diagnostics;
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

    /// <summary>Gets or sets when the job was created (queue order).</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the job reached a terminal state.</summary>
    public DateTime? FinishedAtUtc { get; set; }

    /// <summary>Gets or sets the batch this job belongs to (null for standalone jobs).</summary>
    public string? BatchId { get; set; }

    /// <summary>Gets or sets the 0-based position of this job inside its batch.</summary>
    public int BatchIndex { get; set; } = -1;

    /// <summary>Gets or sets the batch scope label (e.g. "Series · Season 2").</summary>
    public string? BatchLabel { get; set; }

    /// <summary>Gets or sets the human display label (subtitle/track title).</summary>
    public string? Label { get; set; }
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

    /// <summary>Gets or sets the bundled ffsubsync version (plugin-shipped binary), if present.</summary>
    public string? BundledFfSubSyncVersion { get; set; }

    /// <summary>Gets or sets the runtime identifier the bundled binary was built for, if present.</summary>
    public string? BundledRid { get; set; }
}

/// <summary>
/// Service that manages ffsubsync installation and runs sync jobs.
/// </summary>
public class SubSyncService
{
    private readonly ILogger<SubSyncService> _logger;
    private readonly ILibraryManager _libraryManager;
    private readonly ILibraryMonitor _libraryMonitor;
    private readonly ConcurrentDictionary<string, SyncJob> _jobs = new();

    // Track whether an installation is currently in progress
    private int _installing;

    // Single global FIFO queue: every sync (detail-page or batch) is a job in
    // this queue; one background pump runs them strictly one at a time, so
    // overlapping batches can never race on the same subtitle files.
    private readonly object _queueLock = new();
    private readonly List<SyncJob> _runOrder = new();
    private Task? _pumpTask;
    private readonly SemaphoreSlim _wakePump = new(0, 1);
    private readonly ConcurrentDictionary<string, (Video Video, MediaBrowser.Model.Entities.MediaStream Stream, int Ordinal, Configuration.PluginConfiguration Config)> _jobContexts = new();

    // Cleanup timer for evicting old completed/failed jobs
    private readonly Timer _cleanupTimer;

    /// <summary>Allowed values for the --vad config option.</summary>
    private static readonly HashSet<string> AllowedVadMethods = new(StringComparer.OrdinalIgnoreCase)
    {
        "subs", "webrtc", "subs_then_webrtc", "auditok"
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
        var bundled = BundledFfSubSyncPath;
        if (bundled is not null)
        {
            return bundled;
        }

        var config = Plugin.Instance?.Configuration;

        // If user explicitly set a custom path, use it
        if (config is not null
            && !string.IsNullOrWhiteSpace(config.FfSubSyncPath)
            && config.FfSubSyncPath != "ffsubsync")
        {
            return config.FfSubSyncPath;
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

            // Validate venv path is a subdirectory of the expected plugin data path
            var expectedParent = Path.GetFullPath(venvPath);
            if (expectedParent.Contains(".."))
            {
                throw new InvalidOperationException("Venv path contains path traversal characters.");
            }

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

        return source.MediaStreams
            .Where(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle)
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
    /// Starts a sync job for the given item and subtitle stream index.
    /// Automatically ensures ffsubsync is available before running.
    /// </summary>
    /// <param name="itemId">The Jellyfin item ID.</param>
    /// <param name="subtitleIndex">The subtitle stream index within the first media source.</param>
    /// <returns>The created sync job.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the video file is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the subtitle stream is not found or ffsubsync is unavailable.</exception>
    public SyncJob StartSync(Guid itemId, int subtitleIndex)
    {
        return EnqueueSync(itemId, subtitleIndex, label: null, batchId: null, batchLabel: null, batchIndex: -1);
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
    /// <returns>The queued sync job.</returns>
    /// <exception cref="FileNotFoundException">Thrown when the video file is not found.</exception>
    /// <exception cref="InvalidOperationException">Thrown when the subtitle stream is not found.</exception>
    public SyncJob EnqueueSync(Guid itemId, int subtitleIndex, string? label, string? batchId, string? batchLabel, int batchIndex)
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

        // Jellyfin's MediaStream.Index is the stream's index in the CONTAINER
        // (global across video/audio/subtitle). ffmpeg's "-map 0:s:N" needs the
        // ordinal WITHIN subtitle streams — count subtitle streams with a lower
        // container index to derive it. Using the raw Index here made extraction
        // fail with "Failed to set value '0:s:4' for option 'map'" on files that
        // have fewer than Index+1 subtitle tracks.
        var subtitleOrdinal = source.MediaStreams
            .Count(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Subtitle && s.Index < subtitleStream.Index);

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
            Label = label
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
    /// <returns>The created batch jobs (includes pre-failed entries).</returns>
    public IReadOnlyList<SyncJob> CreateBatch(string label, IReadOnlyList<(Guid ItemId, int SubtitleIndex, string? Title)> tasks)
    {
        var batchId = Guid.NewGuid().ToString("N");
        var jobs = new List<SyncJob>(tasks.Count);

        for (var i = 0; i < tasks.Count; i++)
        {
            var task = tasks[i];
            try
            {
                jobs.Add(EnqueueSync(task.ItemId, task.SubtitleIndex, task.Title, batchId, label, i));
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
        while (true)
        {
            SyncJob? job;
            lock (_queueLock)
            {
                job = _runOrder.FirstOrDefault(j => j.Status == SyncJobStatus.Queued);
            }

            if (job is null)
            {
                await _wakePump.WaitAsync().ConfigureAwait(false);
                continue;
            }

            _logger.LogInformation("Pump starting job {JobId} (batch {Batch})", job.Id, job.BatchId ?? "none");
            try
            {
                await RunSyncJobWithContext(job).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Unhandled exception in sync job {JobId}", job.Id);
                job.Status = SyncJobStatus.Failed;
                job.Error = $"Internal error: {ex.Message}";
            }
            finally
            {
                job.FinishedAtUtc = DateTime.UtcNow;
            }
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

        await RunSyncJob(job, ctx.Video, ctx.Stream, ctx.Ordinal, ctx.Config).ConfigureAwait(false);
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

    private async Task RunSyncJob(
        SyncJob job,
        Video video,
        MediaBrowser.Model.Entities.MediaStream subtitleStream,
        int subtitleOrdinal,
        Configuration.PluginConfiguration config)
    {
        job.Status = SyncJobStatus.Running;
        job.Progress = 0.0;

        var videoPath = video.Path;
        var videoDir = Path.GetDirectoryName(videoPath) ?? ".";
        var videoNameNoExt = Path.GetFileNameWithoutExtension(videoPath);
        var videoExt = Path.GetExtension(videoPath);
        var tempDir = Path.Combine(Plugin.Instance?.TempPath ?? Path.GetTempPath(), job.Id);
        Directory.CreateDirectory(tempDir);

        // Paths for the safe atomic-replace workflow
        string? backupPath = null;   // .bak of original file (subtitle or video)
        string? tempOutput = null;   // ffsubsync output in temp dir
        string? tempVideo = null;    // remuxed video in temp dir (embedded only)
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

            if (subtitleStream.IsExternal && !string.IsNullOrEmpty(subtitleStream.Path))
            {
                subtitleInputPath = subtitleStream.Path;
            }
            else
            {
                // Only text subtitles can be aligned — image-based tracks (PGS,
                // DVD/VobSub, DVB, XSUB) cannot be converted to SRT text and made
                // ffmpeg fail with a cryptic exit code (e.g. 234) at extraction.
                var codec = (subtitleStream.Codec ?? string.Empty).ToLowerInvariant();
                if (codec.Contains("pgs") || codec.Contains("dvd") || codec.Contains("xsub")
                    || codec.Contains("dvb") || codec.Contains("vob") || codec.Contains("bitmap"))
                {
                    throw new InvalidOperationException(
                        "This embedded subtitle track is image-based (PGS/DVD/VobSub) and can't be synchronized — only text subtitles can be aligned.");
                }

                job.Phase = "Extracting subtitle";
                job.Progress = 0.05;
                subtitleInputPath = Path.Combine(tempDir, $"subtitle_{job.SubtitleIndex}.srt");
                _logger.LogInformation("Extracting embedded subtitle stream {Index} (ordinal {Ordinal}) from {Video}", job.SubtitleIndex, subtitleOrdinal, videoPath);
                await ExtractSubtitle(videoPath, subtitleOrdinal, subtitleInputPath).ConfigureAwait(false);
            }

            // Step 2: Run ffsubsync → temp output
            job.Phase = "Analyzing speech";
            job.Progress = 0.1;

            tempOutput = Path.Combine(tempDir, "synced.srt");
            var args = BuildFfSubSyncArgs(config, videoPath, subtitleInputPath, tempOutput, tempDir);

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
                CancellationToken.None).ConfigureAwait(false);

            if (exitCode != 0)
            {
                throw new InvalidOperationException($"ffsubsync exited with code {exitCode}.");
            }

            if (!File.Exists(tempOutput))
            {
                throw new InvalidOperationException("ffsubsync completed but output file was not created.");
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
                job.Phase = "Remuxing video";
                job.Progress = 0.75;

                (tempVideo, backupPath) = await ReplaceEmbeddedSubtitle(
                    videoPath, videoDir, videoNameNoExt, videoExt,
                    job.SubtitleIndex, tempOutput).ConfigureAwait(false);

                job.OutputPath = videoPath;
                _logger.LogInformation("Replaced embedded subtitle in video: {Path} (backup at {Backup})", videoPath, backupPath);
            }

            // Step 4: Verify
            job.Phase = "Verifying";
            job.Progress = 0.95;

            var externalTarget = subtitleStream.IsExternal && !string.IsNullOrEmpty(job.OutputPath)
                ? job.OutputPath
                : subtitleStream.Path;

            if (subtitleStream.IsExternal)
            {
                if (!File.Exists(externalTarget) || new FileInfo(externalTarget).Length == 0)
                {
                    throw new InvalidOperationException("Subtitle verification failed — synced output is missing or empty.");
                }
            }
            else
            {
                if (!File.Exists(videoPath) || new FileInfo(videoPath).Length == 0)
                {
                    throw new InvalidOperationException("Video remux verification failed — file is missing or empty after replace.");
                }
            }

            // Step 5: Success — remove backup
            SafeDelete(backupPath);
            backupPath = null;

            job.Phase = "Complete";
            job.Status = SyncJobStatus.Completed;
            job.Progress = 1.0;

            _logger.LogInformation("Sync job {JobId} completed — wrote: {Output}", job.Id, job.OutputPath ?? "(no output path set)");

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
    /// Replaces an embedded subtitle stream in a video file by remuxing with ffmpeg.
    /// Creates a backup of the original video, then atomically renames the new video.
    /// Returns (tempVideoPath, backupPath) so the caller can manage cleanup.
    /// </summary>
    private async Task<(string TempVideo, string BackupPath)> ReplaceEmbeddedSubtitle(
        string videoPath, string videoDir, string videoNameNoExt, string videoExt,
        int subtitleStreamIndex, string syncedSrtPath)
    {
        // Determine output container — keep same extension, fallback to .mkv for safety
        var outputExt = videoExt.ToLowerInvariant();
        if (outputExt is not (".mkv" or ".mp4" or ".webm" or ".ts" or ".mov"))
        {
            // For unusual containers, remux to .mkv which supports all subtitle codecs
            _logger.LogWarning("Container format {Ext} may not support SRT subtitles, remuxing to .mkv", outputExt);
            outputExt = ".mkv";
        }

        var tempVideo = Path.Combine(videoDir, $"{videoNameNoExt}.subsync_tmp{outputExt}");
        var backupPath = videoPath + ".bak.subsync";

        // Build ffmpeg command:
        //   - Copy all streams as-is (no re-encoding)
        //   - Map the synced .srt as a new subtitle stream
        //   - Map all original streams
        //   - Disable the original subtitle stream at index (but keep it for safety)
        //
        // Actually, the safest approach: copy all streams + add the synced SRT as a new stream.
        // Then the user has both the original and synced embedded.
        // But the user asked to REPLACE, so we use -map to exclude the original sub and include the new one.

        var ffmpegPath = ResolveFfmpegPath();

        // Strategy: copy all original streams + add the synced SRT as an additional subtitle.
        // The original (unsynced) subtitle is preserved inside the container for safety.
        // ArgumentList: argv passed directly, no string-escaping layer.
        var args = new List<string>
        {
            "-y",
            "-nostdin",
            "-i", videoPath,
            "-i", syncedSrtPath,
            "-map", "0",            // All original streams (including old subtitle)
            "-map", "1:0",          // The synced SRT
            "-c", "copy",           // No re-encoding, just remux
            tempVideo
        };

        _logger.LogInformation("Remuxing video with synced subtitle: ffmpeg {Args}", string.Join(" ", args));

        var (exitCode, stderr) = await RunProcessArgumentListAsync(ffmpegPath, args, null, CancellationToken.None).ConfigureAwait(false);

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"ffmpeg remux failed: {FfmpegError(stderr)}");
        }

        if (!File.Exists(tempVideo) || new FileInfo(tempVideo).Length == 0)
        {
            throw new InvalidOperationException("ffmpeg remux produced no output file.");
        }

        // Backup original video
        _logger.LogInformation("Backing up original video: {Original} → {Backup}", videoPath, backupPath);
        File.Copy(videoPath, backupPath, overwrite: false);

        // Replace original with new video (atomic on same filesystem)
        _logger.LogInformation("Replacing video with remuxed version: {Temp} → {Original}", tempVideo, videoPath);
        File.Copy(tempVideo, videoPath, overwrite: true);
        SafeDelete(tempVideo);

        return (tempVideo, backupPath);
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

    private string BuildFfSubSyncArgs(Configuration.PluginConfiguration config, string videoPath, string subtitleInput, string subtitleOutput, string? logDir = null)
    {
        // Validate config values to prevent argument injection
        var vadMethod = AllowedVadMethods.Contains(config.VadMethod)
            ? config.VadMethod
            : "subs_then_webrtc";
        var outputEncoding = AllowedOutputEncodings.Contains(config.OutputEncoding)
            ? config.OutputEncoding
            : "utf-8";

        var args = new List<string>
        {
            EscapeArg(videoPath),
            "-i", EscapeArg(subtitleInput),
            "-o", EscapeArg(subtitleOutput),
            $"--max-offset-seconds {config.MaxOffsetSeconds}",
            $"--max-subtitle-seconds {config.MaxSubtitleSeconds}",
            $"--vad {vadMethod}",
            $"--output-encoding {outputEncoding}"
        };

        // Always pass an explicit ffmpeg: Jellyfin's own ffmpeg is auto-detected
        // (env JELLYFIN_FFMPEG or the standard install path), so Docker users
        // never need to configure anything or have ffmpeg on PATH.
        args.Add($"--ffmpeg-path {EscapeArg(ResolveFfmpegPath())}");

        if (config.UseGoldenSectionSearch)
        {
            args.Add("--gss");
        }

        if (!string.IsNullOrWhiteSpace(logDir))
        {
            args.Add($"--log-dir-path {EscapeArg(logDir)}");
        }

        return string.Join(" ", args);
    }

    private async Task ExtractSubtitle(string videoPath, int streamIndex, string outputPath)
    {
        var ffmpegPath = ResolveFfmpegPath();

        // ArgumentList passes argv directly — no string-quoting/escaping layer
        // that can mangle paths into "Error opening output files: Invalid argument".
        var args = new List<string>
        {
            "-y",
            "-nostdin",
            "-i", videoPath,
            "-map", $"0:s:{streamIndex}",
            "-f", "srt",
            outputPath
        };

        _logger.LogInformation("Extracting subtitle: ffmpeg {Args}", string.Join(" ", args));

        var (exitCode, stderr) = await RunProcessArgumentListAsync(ffmpegPath, args, null, CancellationToken.None).ConfigureAwait(false);
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

        process.Start();

        // Kill the process if cancellation is requested
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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

        process.Start();

        // Kill the process if cancellation is requested
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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

        process.Start();

        // Kill the process if cancellation is requested
        using var registration = cancellationToken.Register(() =>
        {
            try { process.Kill(entireProcessTree: true); }
            catch { /* process may have already exited */ }
        });

        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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
        string executable, string arguments, string? workingDir,
        Action<string>? onStderrLine, CancellationToken cancellationToken)
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

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

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
