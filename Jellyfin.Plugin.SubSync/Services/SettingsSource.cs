using System;
using System.IO;
using System.Threading;
using System.Xml.Serialization;
using Jellyfin.Plugin.SubSync.Configuration;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Reads the plugin's settings from the file Jellyfin writes.
///
/// The base class keeps the configuration it loaded when the plugin was constructed, and a saved
/// change does not always reach that copy: the settings page reports "Saved.", the file on disk
/// holds the new values, and the running plugin carries on with the old ones until Jellyfin is
/// restarted. That is indistinguishable from a setting being ignored, and it affected every
/// setting at once (a worker count, a sync mode, a language filter).
///
/// So the settings file is the source of truth: it is re-read whenever its timestamp changes.
/// The base class copy remains the fallback for the moments when the file cannot be read.
/// </summary>
public static class SettingsSource
{
    private static readonly object Gate = new();
    private static DateTime _stampUtc = DateTime.MinValue;
    private static PluginConfiguration? _cached;
    private static long _stats;
    private static long _reads;
    private static long _lastStatMs = long.MinValue;

    /// <summary>
    /// How long the settings file's timestamp is trusted before it is read again.
    /// </summary>
    /// <remarks>
    /// S7: the file is checked by stat-ing it, and the enqueue path asks for the settings several times per queued
    /// task (the enqueue itself, the lane's width, the worker count). Under load each of those was a round trip to
    /// the share that was already busy reading the media, which is the "each job re-probes the storage" half of the
    /// row. A settings change that a hand-edited file makes is therefore visible within this window rather than
    /// instantly; the window is short enough to be indistinguishable from instant, and a save through the API does
    /// not wait for it at all (the settings page applies the change it just wrote, and <c>ApplySettingsNow</c>
    /// resets this cache outright).
    ///
    /// Set <c>SUBSYNC_SETTINGS_STAT_MS=0</c> to stat on every call - the behaviour before this fix, kept so the
    /// cost can be measured on the same build rather than argued about.
    /// </remarks>
    private static readonly long StatTtlMs = ResolveStatTtlMs();

    /// <summary>Gets how many times the settings file has been stat-ed.</summary>
    public static long Stats => Interlocked.Read(ref _stats);

    /// <summary>Gets how many times the settings file has been parsed.</summary>
    public static long Reads => Interlocked.Read(ref _reads);

    /// <summary>
    /// Gets the effective settings, re-reading the settings file when it has changed on disk.
    /// </summary>
    /// <returns>The current configuration, or the plugin's copy when no file is readable.</returns>
    public static PluginConfiguration? Current()
    {
        var fallback = Plugin.Instance?.Configuration;
        var path = Plugin.Instance?.SettingsFilePath;
        if (string.IsNullOrEmpty(path))
        {
            return fallback;
        }

        // S7: the stamp is only re-read when the last reading is old enough to be worth another round trip.
        // With StatTtlMs at 0 (its pre-fix behaviour) this is always true.
        if (!StampIsDue())
        {
            lock (Gate)
            {
                if (_cached is not null)
                {
                    return _cached;
                }
            }

            return fallback;
        }

        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            Interlocked.Increment(ref _stats);
            lock (Gate)
            {
                if (_cached is not null && stamp == _stampUtc)
                {
                    return _cached;
                }

                var reloaded = Read(path);
                if (reloaded is not null)
                {
                    Interlocked.Increment(ref _reads);
                    _stampUtc = stamp;
                    _cached = reloaded;
                    return _cached;
                }
            }
        }
        catch (IOException)
        {
            // Unreadable file: fall through to the plugin's own copy.
        }
        catch (UnauthorizedAccessException)
        {
        }

        return fallback;
    }

    /// <summary>
    /// Claims the right to stat the settings file now, if the last reading is old enough.
    /// </summary>
    /// <returns>True when this call should stat the file.</returns>
    /// <remarks>
    /// The claim is an exchange rather than a check under the settings gate: two callers that both see a stale
    /// reading cost one extra stat between them, while holding the gate across a storage round trip would make
    /// every other caller wait for the share - which is the defect this exists to remove.
    /// </remarks>
    private static bool StampIsDue()
    {
        var now = Environment.TickCount64;
        var last = Interlocked.Read(ref _lastStatMs);
        if (!StampIsDueFor(now, last, StatTtlMs))
        {
            return false;
        }

        Interlocked.Exchange(ref _lastStatMs, now);
        return true;
    }

    /// <summary>
    /// The trust rule itself, as a pure predicate, so it can be checked without a running plugin.
    /// </summary>
    /// <param name="nowMs">The current monotonic millisecond count.</param>
    /// <param name="lastMs">When the file was last stat-ed, or <see cref="long.MinValue"/> for never.</param>
    /// <param name="ttlMs">The trust window; 0 means the file is stat-ed on every call.</param>
    /// <returns>True when the file should be stat-ed now.</returns>
    internal static bool StampIsDueFor(long nowMs, long lastMs, long ttlMs) =>
        ttlMs <= 0 || lastMs == long.MinValue || nowMs - lastMs >= ttlMs;

    /// <summary>Gets the trust window in force, in milliseconds.</summary>
    internal static long StatTtlMsValue => StatTtlMs;

    /// <summary>
    /// Reads the trust window from the environment, defaulting to a quarter of a second.
    /// </summary>
    /// <returns>The window in milliseconds; 0 means "stat on every call".</returns>
    private static long ResolveStatTtlMs()
    {
        var raw = Environment.GetEnvironmentVariable("SUBSYNC_SETTINGS_STAT_MS");
        if (long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out var parsed))
        {
            return Math.Clamp(parsed, 0, 60_000);
        }

        return 250;
    }

    /// <summary>
    /// Deserialises settings from an XML file.
    ///
    /// Separate from <see cref="Current"/> so it can be tested without a running plugin.
    /// </summary>
    /// <param name="path">Path of the settings file.</param>
    /// <returns>The parsed settings, or null when the file is missing or unreadable.</returns>
    public static PluginConfiguration? Read(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        try
        {
            var serializer = new XmlSerializer(typeof(PluginConfiguration));
            using var stream = File.OpenRead(path);
            return serializer.Deserialize(stream) as PluginConfiguration;
        }
        catch (InvalidOperationException)
        {
            // Malformed XML: better to keep running with the last known settings.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Forgets the cached copy, so the next read comes from the file (used by tests).
    /// </summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _cached = null;
            _stampUtc = DateTime.MinValue;
        }

        // The trust window goes with the cache: after a reset the next caller stats the file even if the last
        // reading was a moment ago.
        Interlocked.Exchange(ref _lastStatMs, long.MinValue);
    }
}
