import re

# ---------------------------------------------------------------- service: ultimate
p = 'Jellyfin.Plugin.SubSync/Services/SubSyncService.cs'
s = open(p).read()


def rep(old, new):
    global s
    assert old in s, 'NOT FOUND: ' + old[:90]
    s = s.replace(old, new, 1)


rep("""    private static readonly HashSet<string> AllowedMultiSyncModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "normal", "parallel", "fast"
    };""",
    """    private static readonly HashSet<string> AllowedMultiSyncModes = new(StringComparer.OrdinalIgnoreCase)
    {
        "normal", "parallel", "fast", "ultimate"
    };

    /// <summary>True for modes that run several jobs at once.</summary>
    private static bool IsParallelMode(string mode) =>
        mode is "parallel" or "ultimate";

    /// <summary>True for modes that reuse one media file's speech analysis.</summary>
    private static bool IsSpeechCachingMode(string mode) =>
        mode is "fast" or "ultimate";""")

rep("""                    var headMode = NormalizeMode(head.Mode);
                    var limit = headMode == "parallel"
                        ? NormalizeWorkers(config?.ParallelWorkers ?? 2)
                        : 1;""",
    """                    var headMode = NormalizeMode(head.Mode);
                    var limit = IsParallelMode(headMode)
                        ? NormalizeWorkers(config?.ParallelWorkers ?? DefaultParallelWorkers)
                        : 1;""")

rep("""            if (mode == "fast")
            {
                var engineVersion""",
    """            if (IsSpeechCachingMode(mode))
            {
                var engineVersion""")

rep("""            if (mode == "fast" && speechKey is not null && serializeSpeech)""",
    """            if (IsSpeechCachingMode(mode) && speechKey is not null && serializeSpeech)""")

open(p, 'w').write(s)
print('service: ultimate mode')

# ----------------------------------------------------------- controller: view info
p2 = 'Jellyfin.Plugin.SubSync/Api/SubSyncController.cs'
c = open(p2).read()


def rep2(old, new):
    global c
    assert old in c, 'NOT FOUND: ' + old[:90]
    c = c.replace(old, new, 1)


rep2("""    /// <summary>Gets or sets the batch scope label (or empty).</summary>""",
     """    /// <summary>Gets or sets the multi-subtitle mode the batch runs in.</summary>
    public string Mode { get; set; } = "normal";

    /// <summary>Gets or sets every task currently running (parallel modes can have several).</summary>
    public List<BatchTask> RunningTasks { get; set; } = new();

    /// <summary>Gets or sets the batch scope label (or empty).</summary>""")

rep2("""            CreatedAtUtc = jobs.Min(j => j.CreatedAtUtc),
            FinishedAtUtc = jobs.Max(j => j.FinishedAtUtc),""",
     """            CreatedAtUtc = jobs.Min(j => j.CreatedAtUtc),
            FinishedAtUtc = jobs.Max(j => j.FinishedAtUtc),
            Mode = Services.SyncJobMode.Normalize(jobs[0].Mode),
            RunningTasks = jobs
                .Where(j => j.Status == Services.SyncJobStatus.Running)
                .OrderBy(j => j.BatchIndex)
                .Select(j => new BatchTask
                {
                    BatchIndex = j.BatchIndex,
                    ItemId = j.ItemId,
                    Title = j.Label,
                    Status = j.Status.ToString(),
                    Progress = j.Progress,
                    Phase = j.Phase,
                    Error = j.Error,
                    OutputPath = j.OutputPath,
                    Outcome = j.Outcome
                })
                .ToList(),""")

open(p2, 'w').write(c)
print('controller: mode + running tasks')
