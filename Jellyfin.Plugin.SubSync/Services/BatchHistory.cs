using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace Jellyfin.Plugin.SubSync.Services;

/// <summary>
/// One task of a batch, as it is remembered for the History view.
/// </summary>
public class BatchHistoryJob
{
    /// <summary>Gets or sets the job id.</summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Gets or sets the library item id.</summary>
    public Guid ItemId { get; set; }

    /// <summary>Gets or sets the subtitle's ordinal within the item.</summary>
    public int SubtitleIndex { get; set; }

    /// <summary>Gets or sets the job's mode.</summary>
    public string Mode { get; set; } = "normal";

    /// <summary>Gets or sets the batch this job belongs to.</summary>
    public string? BatchId { get; set; }

    /// <summary>Gets or sets the job's position within its batch.</summary>
    public int BatchIndex { get; set; } = -1;

    /// <summary>Gets or sets the batch's label.</summary>
    public string? BatchLabel { get; set; }

    /// <summary>Gets or sets the job's own label.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets the terminal status, as text, so an unknown value cannot break the load.</summary>
    public string Status { get; set; } = "Completed";

    /// <summary>Gets or sets what the sync did.</summary>
    public string? Outcome { get; set; }

    /// <summary>Gets or sets the file written, when one was.</summary>
    public string? OutputPath { get; set; }

    /// <summary>Gets or sets the failure or refusal text.</summary>
    public string? Error { get; set; }

    /// <summary>Gets or sets how the subtitle was extracted.</summary>
    public string? ExtractionNote { get; set; }

    /// <summary>Gets or sets the job's phase when it finished.</summary>
    public string Phase { get; set; } = "Complete";

    /// <summary>Gets or sets how far the job got.</summary>
    public double Progress { get; set; }

    /// <summary>Gets or sets when the job was created.</summary>
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets when the job finished.</summary>
    public DateTime? FinishedAtUtc { get; set; }
}

/// <summary>
/// One batch, as it is remembered for the History view.
/// </summary>
public class BatchHistoryEntry
{
    /// <summary>Gets or sets the batch id.</summary>
    public string BatchId { get; set; } = string.Empty;

    /// <summary>Gets or sets the batch's label.</summary>
    public string? Label { get; set; }

    /// <summary>Gets or sets when the batch was created.</summary>
    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;

    /// <summary>Gets or sets the tasks of the batch.</summary>
    public List<BatchHistoryJob> Jobs { get; set; } = new();
}

/// <summary>
/// The batch history on disk, so it survives a plugin restart (S25).
/// </summary>
/// <remarks>
/// Batches live in memory in <see cref="SubSyncService"/> and are read back through
/// <c>GET /SubSync/Batches</c>; a restart lost the whole list, which is what "the history resets"
/// meant for the user. This uses the same shape of state file the sweep already keeps: one JSON
/// document, written atomically (temp file then move), never throwing, bounded in size. Losing the
/// file costs the history, never a sync.
/// </remarks>
public static class BatchHistory
{
    /// <summary>How many batches are kept; the oldest are dropped.</summary>
    public const int MaxBatches = 20;

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>Gets the file the plugin keeps the history in.</summary>
    public static string DefaultPath => Path.Combine(
        Plugin.Instance?.StatePath ?? Path.GetTempPath(), "batch-history.json");

    /// <summary>
    /// Writes the given batches, keeping the newest <see cref="MaxBatches"/>.
    /// </summary>
    /// <param name="path">The file to write.</param>
    /// <param name="entries">The batches, oldest first.</param>
    public static void Save(string path, IEnumerable<BatchHistoryEntry> entries)
    {
        try
        {
            var kept = entries
                .OrderByDescending(e => e.CreatedUtc)
                .Take(MaxBatches)
                .OrderBy(e => e.CreatedUtc)
                .ToList();

            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(kept, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception)
        {
            // Persistence is best effort: a failed write costs the history, never a sync.
        }
    }

    /// <summary>
    /// Reads the batches back. A missing, empty or corrupt file is an empty history, not an error.
    /// </summary>
    /// <param name="path">The file to read.</param>
    /// <returns>The batches, oldest first.</returns>
    public static List<BatchHistoryEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return new List<BatchHistoryEntry>();
            }

            var entries = JsonSerializer.Deserialize<List<BatchHistoryEntry>>(File.ReadAllText(path))
                ?? new List<BatchHistoryEntry>();

            return entries
                .Where(e => !string.IsNullOrEmpty(e.BatchId))
                .OrderBy(e => e.CreatedUtc)
                .TakeLast(MaxBatches)
                .ToList();
        }
        catch (Exception)
        {
            // A corrupt history must never block a start-up.
            return new List<BatchHistoryEntry>();
        }
    }

    /// <summary>
    /// Formats the line the plugin logs when it restores history. Pure, so a check can pin it.
    /// </summary>
    /// <param name="batches">How many batches were restored.</param>
    /// <param name="jobs">How many tasks they hold.</param>
    /// <param name="path">Where they came from.</param>
    /// <returns>The log line.</returns>
    public static string DescribeRestore(int batches, int jobs, string path)
        => $"batch history: restored {batches} batch(es), {jobs} task(s) from {path}";
}
