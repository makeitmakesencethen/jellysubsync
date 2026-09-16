using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Where the ffsubsync engine is, what version it answers, and how it is installed.
/// </summary>
/// <remarks>
/// Split out of <c>SubSyncService</c> (the C9 cluster of <c>knowledge/SUBSYNCSERVICE_MAP.md</c>): the resolver
/// (bundled binary, then the configured path, then the managed venv, then PATH), the bundled binary's version
/// cache (B5), the installation status the settings page reads (D2), and the installer that provisions python3
/// and pip-installs the engine. The service keeps the surface the controller and the settings page call, and
/// holds one of these.
/// </remarks>
internal sealed class FfSubSyncEngine
{
    private readonly ILogger _logger;
    private readonly SubSyncProcesses _processes;

    // Track whether an installation is currently in progress
    private int _installing;

    /// <summary>How long a resolved bundled-engine path is trusted before it is checked again (B5/S7).</summary>
    private const long BundlePathRecheckMs = 1000;

    /// <summary>The bundled engine's version, remembered per binary rather than spawned per question (B5).</summary>
    private readonly EngineVersionCache _bundledVersionCache;

    private string? _bundlePath;
    private long _bundlePathCheckedMs;

    /// <summary>Gets how many times the bundled engine was spawned to ask its version (B5).</summary>
    internal long EngineVersionProbes => _bundledVersionCache.Probes;

    /// <summary>Initializes a new instance of the <see cref="FfSubSyncEngine"/> class.</summary>
    /// <param name="logger">Logger the installer and the resolvers report through.</param>
    /// <param name="processes">The process layer the version probes and the installer run through.</param>
    internal FfSubSyncEngine(ILogger logger, SubSyncProcesses processes)
    {
        _logger = logger;
        _processes = processes;

        _bundledVersionCache = new EngineVersionCache(path =>
        {
            var (exitCode, output) = _processes.RunProcessCaptureAsync(path, "--version", null).GetAwaiter().GetResult();
            var version = exitCode == 0 ? output.Trim() : null;
            PluginLog.Info($"engine identity: {path} answered '{version}' (resolved once for this binary)");
            return version;
        });
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

            // B5/S7: this getter used to stat the file *and* re-set its mode on every call, and the version
            // property asked for it every time a cache key was built - a syscall per queued job on the
            // scheduler's own path. Both answers change only when a plugin upgrade replaces the file, so a
            // resolved path is trusted for a second (one stat per second, not per call) and the executable
            // bit is restored once per resolved path.
            var now = Environment.TickCount64;
            var cached = Volatile.Read(ref _bundlePath);
            if (cached is not null
                && string.Equals(cached, candidate, StringComparison.Ordinal)
                && now - Volatile.Read(ref _bundlePathCheckedMs) < BundlePathRecheckMs)
            {
                return cached;
            }

            if (!File.Exists(candidate))
            {
                Volatile.Write(ref _bundlePath, null);
                return null;
            }

            // Jellyfin extracts plugin zips with System.IO.Compression, which does
            // not preserve Unix executable permissions — restore the bit so the
            // bundled PyInstaller launcher can actually be spawned.
            if (!OperatingSystem.IsWindows() && !string.Equals(cached, candidate, StringComparison.Ordinal))
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

            Volatile.Write(ref _bundlePath, candidate);
            Volatile.Write(ref _bundlePathCheckedMs, now);
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

            // B5: the answer is remembered per binary. Before this, asking cost a process spawn - a fork and a
            // PyInstaller bootstrap - and the scheduler asked once per queued job on every planning pass, from a
            // property getter, while the queue waited. The identity is what the speech cache keys on, so the
            // spawns multiplied with the queue rather than with the number of engines.
            DateTime stamp;
            long size;
            try
            {
                var info = new FileInfo(path);
                stamp = info.LastWriteTimeUtc;
                size = info.Length;
            }
            catch (Exception)
            {
                stamp = DateTime.MinValue;
                size = -1;
            }

            return _bundledVersionCache.VersionFor(path, stamp, size);
        }
    }

    /// <summary>
    /// Resolves the ffmpeg path: config → Jellyfin's own ffmpeg → system PATH.
    /// Jellyfin always ships an ffmpeg; auto-detecting it means Docker users
    /// never need to configure anything.
    /// </summary>
    internal string ResolveFfmpegPath()
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
    internal async Task<FfSubSyncInstallationStatus> GetInstallationStatusAsync(string workerSummary)
    {
        // Which engine a job will actually run, and whether it is there (D2). The managed binary is reported
        // separately because the interface offers to install it, but "installed" has to describe the engine in use.
        var enginePath = ResolveFfSubSyncPath();
        var enginePresent = EngineIsInstalled();

        var status = new FfSubSyncInstallationStatus
        {
            VenvPath = Plugin.Instance?.VenvPath,
            ManagedBinaryPath = ManagedFfSubSyncPath,
            ResolvedBinaryPath = enginePath,
            IsInstalled = enginePresent,
            EngineNote = enginePresent
                ? null
                : EngineMissingNote(enginePath),

            // Where the plugin was loaded from and which settings file it reads: two loaded copies
            // would each keep their own configuration, and the settings page would then write to
            // one while the other ran the queue.
            PluginIdentity = Plugin.Instance is { } plugin
                ? plugin.AssemblyLocation + "  ·  " + plugin.SettingsFilePath
                : "plugin instance unavailable",

            // The value in force next to the configured one, so a disagreement is visible without
            // reading any code.
            WorkerSummary = workerSummary,

            // Which build is answering, shown beside the ffsubsync badge instead of inside the
            // progress line.
            PluginVersion = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString() ?? string.Empty,

            // Where the plugin's own log is, so it can be opened from the interface.
            LogFile = PluginLog.Describe(),

            // What the extracted-subtitle cache holds and what bounds it, reported with the audio cache so the two are
            // described in the same place (F16).
            SubtitleCacheSummary = SubtitleCache.Describe()
        };

        // Check system python3
        try
        {
            var (exitCode, stdout) = await _processes.RunProcessCaptureAsync("python3", "--version", null).ConfigureAwait(false);
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
                var (exitCode, stdout) = await _processes.RunProcessCaptureAsync(ManagedFfSubSyncPath, "--version", null).ConfigureAwait(false);
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
                var (exitCode, output) = await _processes.RunProcessCaptureAsync("python3", $"-m venv {EscapeArg(venvPath)}", null).ConfigureAwait(false);
                if (exitCode != 0)
                {
                    throw new InvalidOperationException($"Failed to create virtualenv: {output}");
                }
            }

            // Step 2: Upgrade pip
            _logger.LogInformation("Upgrading pip in virtualenv");
            var pipExit = await _processes.RunProcessAsync(ManagedPythonPath, "-m pip install --upgrade pip", null, cancellationToken).ConfigureAwait(false);
            if (pipExit != 0)
            {
                _logger.LogWarning("pip upgrade failed with exit code {Code}, continuing anyway", pipExit);
            }

            // Step 3: Install ffsubsync + pin setuptools<81 (webrtcvad needs pkg_resources)
            _logger.LogInformation("Installing ffsubsync into virtualenv");
            var installExit = await _processes.RunProcessAsync(ManagedPipPath, "install ffsubsync \"setuptools<81\"", null, cancellationToken).ConfigureAwait(false);
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

    /// <summary>Identity of the engine that shapes a speech signal.</summary>
    /// <remarks>
    /// B5: this is called while a cache key is built, once per queued job on every planning pass, so both halves
    /// are cheap on purpose - the version is remembered per binary (<see cref="EngineVersionCache"/>, which spawns
    /// the engine once) and the plugin's own version is read from the assembly's name.
    /// </remarks>
    internal string EngineIdentity() => string.Join(
        "|",
        BundledFfSubSyncVersion,
        typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "0.0.0.0");

    /// <summary>
    /// Explains where the engine was looked for, when it was not found (D2).
    /// </summary>
    /// <param name="enginePath">The path the resolver chose.</param>
    /// <returns>A sentence naming the path, so a user can act on it.</returns>
    internal string EngineMissingNote(string? enginePath)
    {
        var configured = Services.SettingsSource.Current()?.FfSubSyncPath;
        if (!string.IsNullOrWhiteSpace(configured)
            && !string.IsNullOrWhiteSpace(enginePath)
            && configured == enginePath)
        {
            return $"The configured ffsubsync path {enginePath} does not exist. Correct it in the settings, clear it to "
                + "use the bundled engine, or install the managed one here.";
        }

        return string.IsNullOrWhiteSpace(enginePath)
            ? "No ffsubsync engine could be resolved."
            : $"{enginePath} was not found, so a sync would fail at the first engine call.";
    }

    /// <summary>
    /// Says whether the ffsubsync engine that would actually run can be found (D2).
    /// </summary>
    /// <remarks>
    /// The status used to answer "is the plugin's own managed binary present", which says nothing about the binary a
    /// job will execute: a configured path wins over the managed one, and a bundled binary wins over both. A user with
    /// a configured path that does not exist read "installed", saw no install prompt, and then watched every sync
    /// fail. This asks the resolver.
    /// </remarks>
    /// <returns>True when the resolved engine exists.</returns>
    public bool EngineIsInstalled()
        => EngineIsUsable(
            ResolveFfSubSyncPath(),
            File.Exists,
            Environment.GetEnvironmentVariable("PATH"));

    /// <summary>
    /// Says whether a resolved engine path can be found (D2).
    /// </summary>
    /// <remarks>
    /// A rooted path is asked of the filesystem. A bare command name (the last resort of the resolver: plain
    /// <c>ffsubsync</c>) is looked up on PATH, because that is how the operating system will look for it - and an empty
    /// PATH finds nothing, so this fails closed.
    /// </remarks>
    /// <param name="resolvedPath">The path the resolver chose.</param>
    /// <param name="fileExists">How to ask whether a file exists.</param>
    /// <param name="pathVariable">The PATH the operating system would use.</param>
    /// <returns>True when the engine would be found.</returns>
    internal static bool EngineIsUsable(string? resolvedPath, Func<string, bool> fileExists, string? pathVariable)
    {
        if (string.IsNullOrWhiteSpace(resolvedPath))
        {
            return false;
        }

        if (Path.IsPathRooted(resolvedPath))
        {
            return fileExists(resolvedPath);
        }

        if (string.IsNullOrWhiteSpace(pathVariable))
        {
            return false;
        }

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (!string.IsNullOrWhiteSpace(directory) && fileExists(Path.Join(directory, resolvedPath)))
            {
                return true;
            }
        }

        return false;
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

        var (uidExit, uidOutput) = await _processes.RunProcessCaptureAsync("id", "-u", null).ConfigureAwait(false);
        var isRoot = uidExit == 0 && uidOutput.Trim() == "0";
        if (!isRoot)
        {
            throw new InvalidOperationException(
                "python3 with venv support is missing and the Jellyfin process is not running as root, " +
                "so the plugin cannot install it automatically. Run Jellyfin as root (the default in the " +
                "official Docker image) and retry, or install it manually with: " +
                "apt-get install -y python3 python3-venv");
        }

        var (updateExit, updateOutput) = await _processes.RunProcessCaptureAsync("apt-get", "update", null).ConfigureAwait(false);
        if (updateExit != 0)
        {
            _logger.LogWarning("apt-get update failed during python3 provisioning (continuing anyway): {Output}", Truncate(updateOutput, 800));
        }

        var (installExit, installOutput) = await _processes.RunProcessCaptureAsync(
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
            var (exitCode, _) = await _processes.RunProcessCaptureAsync("python3", $"-c {EscapeArg("import sys, venv, ensurepip")}", null).ConfigureAwait(false);
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
