p = 'Jellyfin.Plugin.SubSync/Services/SubSyncService.cs'
s = open(p).read()


def rep(old, new):
    global s
    assert old in s, 'NOT FOUND: ' + old[:130]
    s = s.replace(old, new, 1)


rep("""    /// <summary>Gets or sets the wave size limit (parallel workers).</summary>
    public int Limit { get; set; } = 4;

    /// <summary>Gets or sets how many heavy readers a single volume may have in one wave.</summary>
    public int HeavyIoPerVolume { get; set; } = 2;

    /// <summary>Gets or sets a predicate telling whether a job reads heavily from storage.</summary>
    public Func<SyncJob, bool>? IsHeavyIo { get; set; }

    /// <summary>Gets or sets the storage volume a job's media lives on.</summary>
    public Func<SyncJob, string>? VolumeOf { get; set; }""",
"""    /// <summary>Gets or sets the wave size limit (parallel workers).</summary>
    public int Limit { get; set; } = 4;

    /// <summary>Gets or sets a predicate telling whether a job reads heavily from storage.</summary>
    public Func<SyncJob, bool>? IsHeavyIo { get; set; }""")

# the wave loop itself: no volume accounting, no per-volume budget
rep("""        var heavyBudget = policy.HeavyIoPerVolume < 1 ? 1 : policy.HeavyIoPerVolume;""",
"""        _ = policy.IsHeavyIo; // per-file guard only; storage scheduling is left to the OS""")

rep("""            var heavy = policy.IsHeavyIo?.Invoke(candidate) ?? false;
            var volume = policy.VolumeOf?.Invoke(candidate) ?? "unknown";

            if (heavy && heavyPerVolume.TryGetValue(volume, out var already) && already >= heavyBudget)
            {
                continue; // this volume already has its share of heavy readers in this wave
            }

            if (!claimedItems.Add(candidate.ItemId))
            {
                // Another subtitle of a file already in this wave: allowed only when that
                // file's speech analysis is cached (no read, just CPU).
                if (heavy || policy.CanShareMediaFile?.Invoke(candidate) != true)
                {
                    continue;
                }
            }

            if (heavy)
            {
                heavyPerVolume[volume] = heavyPerVolume.TryGetValue(volume, out var current) ? current + 1 : 1;
            }

            wave.Add(candidate);""",
"""            var heavy = policy.IsHeavyIo?.Invoke(candidate) ?? false;

            if (!claimedItems.Add(candidate.ItemId))
            {
                // Another subtitle of a file already in this wave: allowed only when that
                // file's speech analysis is cached, so both workers use the same cached
                // reference instead of racing to build it.
                if (heavy || policy.CanShareMediaFile?.Invoke(candidate) != true)
                {
                    continue;
                }
            }

            wave.Add(candidate);""")

rep("""        var policy = new WavePolicy
        {
            Limit = Math.Clamp(config?.ParallelWorkers ?? 4, 1, 16),
            HeavyIoPerVolume = Math.Clamp(config?.HeavyReadsPerVolume ?? 2, 1, 4),
            CanShareMediaFile = job => SpeechIsCached(job),
            IsHeavyIo = job => JobNeedsHeavyIo(job, headMode),
            VolumeOf = job => VolumeOf(job)
        };""",
"""        var policy = new WavePolicy
        {
            Limit = Math.Clamp(config?.ParallelWorkers ?? 4, 1, 16),
            CanShareMediaFile = job => SpeechIsCached(job),
            IsHeavyIo = job => JobNeedsHeavyIo(job, headMode)
        };""")

open(p, 'w').write(s)
print('service: volume gate removed')

# ---------------- config property + settings control gone
p2 = 'Jellyfin.Plugin.SubSync/Configuration/PluginConfiguration.cs'
c = open(p2).read()
import re
before = len(c)
c = re.sub(r"\n[ \t]*///[^\n]*\n[ \t]*public int HeavyReadsPerVolume \{[^}]*\}\n", "\n", c, count=1)
if c == open(p2).read():
    c = re.sub(r"\n[ \t]*(///[^\n]*\n[ \t]*)*public int HeavyReadsPerVolume \{[^}]*\}\n", "\n", c, count=1)
assert 'HeavyReadsPerVolume' not in c, 'config property not removed'
open(p2, 'w').write(c)
print('config: HeavyReadsPerVolume removed')
