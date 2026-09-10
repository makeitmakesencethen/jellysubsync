"""Surface the effective worker limit so "why only 2 in parallel?" answers itself.

The wave selector provably fills the configured count (harness: a 20-episode batch with two
subtitles per episode on one disk selects 4, or 8 when asked). So if fewer jobs run at once,
either the configured value is lower than expected or the queue held fewer eligible tasks.
Both are now visible: the batch view reports the limit, the run line shows running/limit, and
the pump logs every wave it starts.
"""
import re

# ---------------------------------------------------------------- controller: expose the limit
p = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Api/SubSyncController.cs'
c = open(p).read()

old = """    /// <summary>Gets or sets the tasks currently running in this batch.</summary>
    public List<BatchTask> RunningTasks { get; set; } = new();"""
new = """    /// <summary>Gets or sets the tasks currently running in this batch.</summary>
    public List<BatchTask> RunningTasks { get; set; } = new();

    /// <summary>
    /// Gets or sets how many tasks this batch may run at once (the configured worker count for
    /// parallel strategies). Shown next to the running count so a lower-than-expected
    /// parallelism can be traced to the setting instead of guessed at.
    /// </summary>
    public int WorkerLimit { get; set; }"""
assert old in c
c = c.replace(old, new, 1)

old2 = """            Mode = Services.SyncJobMode.Normalize(jobs[0].Mode),
            RunningTasks = jobs"""
new2 = """            Mode = Services.SyncJobMode.Normalize(jobs[0].Mode),
            WorkerLimit = Services.SyncJobMode.IsParallel(Services.SyncJobMode.Normalize(jobs[0].Mode))
                ? Math.Clamp(_syncService.EffectiveWorkerLimit, 1, 16)
                : 1,
            RunningTasks = jobs"""
assert old2 in c
c = c.replace(old2, new2, 1)
open(p, 'w').write(c)
print('batch view exposes the worker limit')

# ---------------------------------------------------------------- service: expose + log it
p = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Services/SubSyncService.cs'
s = open(p).read()

old3 = """    private static int NormalizeWorkers(int workers) => workers < 1 ? 1 : (workers > 8 ? 8 : workers);"""
new3 = """    private static int NormalizeWorkers(int workers) => workers < 1 ? 1 : (workers > 8 ? 8 : workers);

    /// <summary>
    /// Gets how many jobs may run at once with the current settings. Reported to the UI so the
    /// effective parallelism is visible rather than inferred.
    /// </summary>
    public int EffectiveWorkerLimit =>
        NormalizeWorkers(Plugin.Instance?.Configuration?.ParallelWorkers ?? DefaultParallelWorkers);"""
assert old3 in s
s = s.replace(old3, new3, 1)

old4 = """                    jobs = SelectWave(
                        _runOrder.Where(j => j.Status == SyncJobStatus.Queued),
                        headMode,
                        head.BatchId,"""
new4 = """                    jobs = SelectWave(
                        _runOrder.Where(j => j.Status == SyncJobStatus.Queued),
                        headMode,
                        head.BatchId,"""
assert old4 in s

# log each wave: how many jobs, what limit, which mode, and how many are still queued
old5 = """            if (jobs.Count == 0)
            {
                if (_disposing)
                {
                    break;
                }

                await _wakePump.WaitAsync().ConfigureAwait(false);
                continue;
            }"""
new5 = """            if (jobs.Count == 0)
            {
                if (_disposing)
                {
                    break;
                }

                await _wakePump.WaitAsync().ConfigureAwait(false);
                continue;
            }

            if (jobs.Count > 0)
            {
                var stillQueued = 0;
                lock (_queueLock)
                {
                    stillQueued = _runOrder.Count(j => j.Status == SyncJobStatus.Queued);
                }

                _logger.LogInformation(
                    "Wave: starting {Count} job(s) (worker limit {Limit}, mode {Mode}, {Queued} still queued) for batch {Batch}",
                    jobs.Count, EffectiveWorkerLimit, NormalizeMode(jobs[0].Mode), stillQueued, jobs[0].BatchId ?? "(standalone)");
            }"""
assert old5 in s
s = s.replace(old5, new5, 1)
open(p, 'w').write(s)
print('service: worker limit exposed and every wave logged')

# ---------------------------------------------------------------- UI: running/limit
p = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Web/subsyncMain.html'
h = open(p).read()
old6 = """                        if (workersNow > 1) bits.push(workersNow + ' workers');"""
new6 = """                        var workerLimit = view.WorkerLimit || view.workerLimit || 0;
                        if (workersNow > 1 || (workerLimit > 1 && total > workersNow)) {
                            bits.push(workerLimit > 0 ? workersNow + '/' + workerLimit + ' workers' : workersNow + ' workers');
                        }"""
assert old6 in h
h = h.replace(old6, new6, 1)
open(p, 'w').write(h)
print('run line shows running/limit workers')
