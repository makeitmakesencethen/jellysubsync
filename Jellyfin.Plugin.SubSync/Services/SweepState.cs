using System.Security.Cryptography;
using System.Text.Json;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// A single remembered sweep outcome for one external subtitle file.
/// </summary>
public class SweepEntry
{
    /// <summary>Gets or sets the source subtitle file path.</summary>
    public string SourcePath { get; set; } = string.Empty;

    /// <summary>Gets or sets the SHA-256 of the source file at last evaluation.</summary>
    public string SourceHash { get; set; } = string.Empty;

    /// <summary>Gets or sets consecutive execution failures for this exact content (reset on hash change).</summary>
    public int FailStreak { get; set; }

    /// <summary>Gets or sets the last failure message, if any.</summary>
    public string? LastError { get; set; }

    /// <summary>Gets or sets the output file written by the last successful sync.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets when this entry was last touched (used for pruning).</summary>
    public DateTime LastTouchedUtc { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Persistent skip-cache + fail-streak cache for the library sweep.
/// </summary>
/// <remarks>
/// Keyed by source subtitle path; each entry stores the content hash of the
/// source file at last evaluation. If the file content changes, the hash
/// mismatches and the entry is treated as fresh (skip decision and fail
/// streak both reset). Modeled after the skip/fail cache in
/// Marnalas/jellyfin-subsync (MIT), reimplemented for the in-process engine.
/// </remarks>
public class SweepState
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _lock = new();
    private readonly string _filePath;
    private Dictionary<string, SweepEntry> _entries;

    /// <summary>
    /// Initializes a new instance of the <see cref="SweepState"/> class.
    /// </summary>
    /// <param name="filePath">JSON state file path.</param>
    public SweepState(string filePath)
    {
        _filePath = filePath;
        _entries = Load(filePath);
    }

    /// <summary>
    /// Gets the max number of entries kept in the state file.
    /// </summary>
    public const int MaxEntries = 5000;

    /// <summary>
    /// Looks up an entry for a source path.
    /// </summary>
    public SweepEntry? Get(string sourcePath)
    {
        lock (_lock)
        {
            return _entries.TryGetValue(sourcePath, out var entry) ? Clone(entry) : null;
        }
    }

    /// <summary>
    /// Records an outcome for a source file, computing its content hash.
    /// A hash change resets the fail streak and the prior success record.
    /// </summary>
    /// <param name="sourcePath">The external subtitle file path.</param>
    /// <param name="ok">Whether the sync succeeded.</param>
    /// <param name="outputPath">Output path on success (unused on failure).</param>
    /// <param name="error">Failure message on failure.</param>
    public void Record(string sourcePath, bool ok, string? outputPath, string? error)
    {
        string? hash = null;
        try
        {
            hash = HashFile(sourcePath);
        }
        catch
        {
            // File disappeared mid-run; keep any existing entry but do not crash.
        }

        lock (_lock)
        {
            _entries.TryGetValue(sourcePath, out var entry);
            if (entry is null)
            {
                entry = new SweepEntry { SourcePath = sourcePath };
                _entries[sourcePath] = entry;
            }

            var sameContent = hash is not null && string.Equals(hash, entry.SourceHash, StringComparison.Ordinal);
            if (hash is null)
            {
                // Cannot hash now (file gone). Keep existing state untouched except time.
                entry.LastTouchedUtc = DateTime.UtcNow;
                SaveLocked();
                return;
            }

            if (!sameContent)
            {
                entry.FailStreak = 0;
                entry.LastError = null;
                entry.SourceHash = hash;
            }

            if (ok)
            {
                entry.FailStreak = 0;
                entry.LastError = null;
                entry.OutputPath = outputPath;
            }
            else
            {
                entry.FailStreak = sameContent ? entry.FailStreak + 1 : 1;
                entry.LastError = error;
            }

            entry.LastTouchedUtc = DateTime.UtcNow;
            SaveLocked();
        }
    }

    /// <summary>
    /// SHA-256 hex digest of a file's content.
    /// </summary>
    public static string? HashFile(string path)
    {
        using var stream = File.OpenRead(path);
        var bytes = SHA256.HashData(stream);
        return Convert.ToHexStringLower(bytes);
    }

    private static Dictionary<string, SweepEntry> Load(string filePath)
    {
        try
        {
            if (!File.Exists(filePath))
            {
                return new Dictionary<string, SweepEntry>(StringComparer.Ordinal);
            }

            var entries = JsonSerializer.Deserialize<Dictionary<string, SweepEntry>>(File.ReadAllText(filePath))
                ?? new Dictionary<string, SweepEntry>(StringComparer.Ordinal);

            // Bound memory: drop oldest entries beyond the cap.
            if (entries.Count > MaxEntries)
            {
                var toDrop = entries.Count - MaxEntries;
                foreach (var key in entries.OrderBy(kvp => kvp.Value.LastTouchedUtc).Take(toDrop).Select(kvp => kvp.Key).ToList())
                {
                    entries.Remove(key);
                }
            }

            return entries;
        }
        catch (Exception)
        {
            // A corrupt state file must never block the sweep — start fresh.
            return new Dictionary<string, SweepEntry>(StringComparer.Ordinal);
        }
    }

    private void SaveLocked()
    {
        try
        {
            var dir = Path.GetDirectoryName(_filePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = _filePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception)
        {
            // Persistence failure is non-fatal: the sweep still runs this run.
        }
    }

    private static SweepEntry Clone(SweepEntry entry) => new()
    {
        SourcePath = entry.SourcePath,
        SourceHash = entry.SourceHash,
        FailStreak = entry.FailStreak,
        LastError = entry.LastError,
        OutputPath = entry.OutputPath,
        LastTouchedUtc = entry.LastTouchedUtc
    };
}
