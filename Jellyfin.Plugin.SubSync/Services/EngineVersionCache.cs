using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// Remembers what `ffsubsync --version` said, so the answer costs one process spawn per binary instead of one
/// per question.
/// </summary>
/// <remarks>
/// B5: the version is part of the engine's identity, and the identity is part of every speech-cache key, so the
/// scheduler asked for it once per queued job on every planning pass. Each question was answered by *spawning the
/// engine* - a fork, a PyInstaller bootstrap and a pipe read, synchronously, on the dispatch path - and the same
/// answer was thrown away each time. Measured on the rig with a counting wrapper in front of the bundled binary
/// (scenario `s7-queue-load`), a burst of 27 queued tasks while the queue was being worked spawned the engine
/// three times for `--version`; with this cache in place it is spawned once for the whole run.
///
/// The cache is keyed on what identifies the binary (its path, its size and its modification time) rather than on
/// time: a plugin upgrade replaces the file, the key changes, and the new engine is asked once. There is no TTL to
/// guess at, and no way for a replaced engine's version to be reported as the running one's.
///
/// The probe is injected rather than called directly so the rule can be checked without spawning anything: the
/// checks drive this type with a counting delegate and assert one probe for many questions, which is the property
/// the fix is about. The service hands it the plugin's own process runner, so the shipped path and the checked
/// path are the same shape.
/// </remarks>
internal sealed class EngineVersionCache
{
    private readonly Func<string, string?> _probe;
    private readonly object _gate = new();
    private readonly Dictionary<string, string?> _byKey = new(StringComparer.Ordinal);

    private long _probes;

    /// <summary>Initialises a new instance of the <see cref="EngineVersionCache"/> class.</summary>
    /// <param name="probe">Takes the binary's path and returns its version, or null when it cannot be read.</param>
    public EngineVersionCache(Func<string, string?> probe)
    {
        _probe = probe;
    }

    /// <summary>Gets how many times the binary was actually spawned.</summary>
    public long Probes => System.Threading.Interlocked.Read(ref _probes);

    /// <summary>Gets how many questions were answered from the cache.</summary>
    public long Hits { get; private set; }

    /// <summary>
    /// Builds the key that identifies one binary: its path, its size and its modification time.
    /// </summary>
    /// <param name="path">Path of the bundled engine.</param>
    /// <param name="stampUtc">Its last write time, or <see cref="DateTime.MinValue"/> when it could not be read.</param>
    /// <param name="size">Its length in bytes, or -1 when it could not be read.</param>
    /// <returns>A key that changes exactly when the binary does.</returns>
    public static string KeyFor(string path, DateTime stampUtc, long size) =>
        string.Concat(path, "|", stampUtc.Ticks.ToString(System.Globalization.CultureInfo.InvariantCulture), "|",
                      size.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Gets the version of the engine at <paramref name="path"/>, spawning it only when this binary has not been
    /// asked about before.
    /// </summary>
    /// <param name="path">Path of the bundled engine.</param>
    /// <param name="stampUtc">Its last write time, or <see cref="DateTime.MinValue"/> when it could not be read.</param>
    /// <param name="size">Its length in bytes, or -1 when it could not be read.</param>
    /// <returns>The reported version, or null when the engine cannot be run.</returns>
    public string? VersionFor(string path, DateTime stampUtc, long size)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var key = KeyFor(path, stampUtc, size);
        lock (_gate)
        {
            if (_byKey.TryGetValue(key, out var known))
            {
                Hits++;
                return known;
            }
        }

        string? version;
        try
        {
            System.Threading.Interlocked.Increment(ref _probes);
            version = _probe(path);
        }
        catch (Exception)
        {
            // An engine that cannot be run has no version; the sync itself reports the useful error.
            version = null;
        }

        lock (_gate)
        {
            _byKey[key] = version;

            // One binary is one version: keys for anything that was there before are dropped, so a long-lived
            // server that has seen several upgrades does not hold a growing table of engines it no longer has.
            foreach (var stale in new List<string>(_byKey.Keys))
            {
                if (!string.Equals(stale, key, StringComparison.Ordinal))
                {
                    _byKey.Remove(stale);
                }
            }
        }

        return version;
    }
}
