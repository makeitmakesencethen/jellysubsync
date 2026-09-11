using Jellyfin.Plugin.SubSync.Configuration;
using Jellyfin.Plugin.SubSync.Services;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.SubSync;

/// <summary>
/// The SubSync plugin for Jellyfin — synchronizes subtitles with video audio using ffsubsync.
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    private readonly Guid _id = new("c7d8e9f0-a1b2-4c3d-e5f6-a7b8c9d0e1f2");

    private readonly ILogger<Plugin> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    /// <param name="applicationPaths">The application paths.</param>
    /// <param name="xmlSerializer">The XML serializer.</param>
    /// <param name="loggerFactory">Logger factory.</param>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer, ILoggerFactory loggerFactory)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        _logger = loggerFactory.CreateLogger<Plugin>();

        // A reference subtitle must never outlive the run that made it, so anything an interrupted
        // run left in the reference directory is cleared before the queue can start.
        ReferenceStore.SweepLeftovers();

        // Stated at startup because a settings change that appears to do nothing is otherwise
        // invisible: this line shows where the plugin was loaded from, which file it reads, and
        // the values it actually has.
        try
        {
            _logger.LogInformation(
                "SubSync {Version} loaded from {Assembly}; settings file {Config}; parallel workers setting {Workers}",
                GetType().Assembly.GetName().Version?.ToString() ?? "unknown",
                AssemblyLocation,
                SettingsFilePath,
                Configuration?.ParallelWorkers);

            // The plugin log starts with the same facts, so a log file handed over for debugging says
            // which build wrote it and where its settings live.
            PluginLog.Info(
                $"startup: version={GetType().Assembly.GetName().Version?.ToString() ?? "unknown"} "
                + $"assembly={AssemblyLocation} settings={SettingsFilePath} "
                + $"workers={Configuration?.ParallelWorkers} mode={Configuration?.MultiSyncMode} "
                + $"vad={Configuration?.VadMethod} fixFramerate={Configuration?.FixFramerate} log={PluginLog.FilePath}");
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "SubSync startup diagnostics unavailable");
        }

        try
        {
            // Configuration is nullable in the base class; with nothing loaded there is nothing
            // to migrate.
            var stored = Configuration;
            if (stored is not null && Migrate(stored))
            {
                UpdateConfiguration(stored); // persist the migrated values
            }
        }
        catch (Exception ex)
        {
            // A failed migration must never stop the plugin from loading.
            _logger.LogWarning(ex, "SubSync configuration migration failed; continuing with the stored settings");
        }
    }

    /// <inheritdoc />
    public override void UpdateConfiguration(BasePluginConfiguration configuration)
    {
        if (configuration is PluginConfiguration pluginConfiguration)
        {
            Migrate(pluginConfiguration);
        }

        base.UpdateConfiguration(configuration);
    }

    /// <summary>
    /// Brings an older configuration onto current defaults. Runs once per revision and is
    /// persisted immediately, so an install that never opens the settings page still gets
    /// the new behaviour.
    /// </summary>
    /// <param name="config">Configuration to migrate.</param>
    /// <returns>True when something changed and should be saved.</returns>
    private bool Migrate(PluginConfiguration config)
    {
        var changed = false;

        if (config.ConfigVersion < 1)
        {
            // Before the automatic strategy existed, every stored mode was either a legacy
            // default or a deliberate choice. The automatic strategy resolves to the same
            // thing or better for each run shape (single task, several subtitles of one
            // file, several files), so older values are moved onto it once.
            _logger.LogInformation(
                "SubSync config migration: MultiSyncMode '{Old}' -> '{New}' (automatic strategy)",
                config.MultiSyncMode,
                Services.SyncJobMode.Auto);
            config.MultiSyncMode = Services.SyncJobMode.Auto;
            config.ConfigVersion = 1;
            changed = true;
        }

        return changed;
    }

    /// <inheritdoc />
    public override string Name => "SubSync";

    /// <inheritdoc />
    public override Guid Id => _id;

    /// <summary>
    /// Gets the current plugin instance.
    /// </summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>
    /// Gets the temp working directory for SubSync operations.
    /// </summary>
    public string TempPath => Path.Join(ApplicationPaths.CachePath, "subsync");

    /// <summary>
    /// Gets the path to the managed Python virtualenv directory.
    /// </summary>
    public string VenvPath => Path.Join(ApplicationPaths.DataPath, "subsync", "venv");

    /// <summary>
    /// Gets the persistent state directory for sweep caches.
    /// </summary>
    public string StatePath => Path.Join(ApplicationPaths.DataPath, "subsync", "state");

    /// <summary>
    /// Gets the directory holding this plugin's own log file.
    ///
    /// Deliberately under the plugin's data folder rather than its installation folder: Jellyfin
    /// removes the old version's folder when a plugin is updated, so a log written there would
    /// disappear exactly when it is wanted.
    /// </summary>
    public string LogPath => Path.Join(ApplicationPaths.DataPath, "subsync", "logs");

    /// <summary>
    /// Gets where this plugin's own assembly was loaded from.
    ///
    /// Surfaced because two loaded copies of the plugin (an older folder that was not removed
    /// during an update, say) would each hold their own configuration: the settings page would
    /// write to one while the other ran the queue, and a changed setting would appear to be
    /// ignored.
    /// </summary>
    public string AssemblyLocation => typeof(Plugin).Assembly.Location;

    /// <summary>
    /// Gets the file this plugin's settings are read from and written to.
    /// </summary>
    public string SettingsFilePath => ConfigurationFilePath;

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        return new[]
        {
            new PluginPageInfo
            {
                Name = "SubSync",
                DisplayName = "SubSync Configuration",
                EmbeddedResourcePath = GetType().Namespace + ".Web.configPage.html",
                EnableInMainMenu = false,
                MenuSection = "server"
            },
            new PluginPageInfo
            {
                Name = "subsync-main",
                DisplayName = "SubSync",
                EmbeddedResourcePath = GetType().Namespace + ".Web.subsyncMain.html",
                EnableInMainMenu = true,
                MenuSection = "plugins",
                MenuIcon = "subtitles"
            }
        };
    }
}
