p = 'Jellyfin.Plugin.SubSync/Services/SubSyncService.cs'
s = open(p).read()


def rep(old, new):
    global s
    assert old in s, 'NOT FOUND: ' + old[:130]
    s = s.replace(old, new, 1)


rep("""        /// <summary>Gets or sets a predicate identifying the storage volume of a job.</summary>
        public Func<SyncJob, string>? VolumeOf { get; set; }

        /// <summary>
        /// Gets or sets how many jobs may read heavily from one volume in the same wave.
        /// One is safest for a single spinning disk or a saturated link but leaves workers
        /// idle; the default allows a little overlap without letting four readers fight over
        /// one device.
        /// </summary>
        public int HeavyIoPerVolume { get; set; } = 2;

        /// <summary>
        /// Gets or sets a predicate saying whether a job will read a lot of data (embedded
        /// extraction or an audio analysis whose result is not cached). Only one such job
        /// runs per volume per wave — otherwise every worker on that disk crawls at once.
        /// </summary>
        public Func<SyncJob, bool>? IsHeavyIo { get; set; }""",
"""        /// <summary>
        /// Gets or sets a predicate saying whether a job needs the file's speech analysis
        /// built (embedded extraction or an uncached audio pass). Used only to decide whether
        /// a second job may join the wave for the same file — storage scheduling itself is
        /// left to the OS, which sees the real device queue.
        /// </summary>
        public Func<SyncJob, bool>? IsHeavyIo { get; set; }""")

rep("""        var heavyBudget = policy.HeavyIoPerVolume < 1 ? 1 : policy.HeavyIoPerVolume;""",
"""        // Waves are bounded by the worker count alone: no per-volume budget, so a wave is
        // never throttled below what the queue asks for.""")
s = s.replace("""        var heavyPerVolume = new Dictionary<string, int>(StringComparer.Ordinal);\n""", "", 1)

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
                // file's speech analysis is cached, so both workers share one reference
                // instead of racing to build it.
                if (heavy || policy.CanShareMediaFile?.Invoke(candidate) != true)
                {
                    continue;
                }
            }

            wave.Add(candidate);""")

rep("""            VolumeOf = job => MediaVolume.Of(_jobContexts.TryGetValue(job.Id, out var c) ? c.Video.Path : null),\n""", "")
rep("""                            VolumeOf = job => MediaVolume.Of(_jobContexts.TryGetValue(job.Id, out var c) ? c.Video.Path : null),\n""", "")
rep("""            HeavyIoPerVolume = Math.Clamp(config?.HeavyReadsPerVolume ?? 2, 1, 4),\n""", "")

open(p, 'w').write(s)
print('service: volume gate removed')
assert 'HeavyIoPerVolume' not in s and 'heavyPerVolume' not in s and 'VolumeOf' not in s, 'leftovers'
assert 'HeavyReadsPerVolume' not in s, 'config read left over'
