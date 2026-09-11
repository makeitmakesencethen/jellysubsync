using System;
using System.IO;
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

        try
        {
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (Gate)
            {
                if (_cached is not null && stamp == _stampUtc)
                {
                    return _cached;
                }

                var reloaded = Read(path);
                if (reloaded is not null)
                {
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
    }
}
