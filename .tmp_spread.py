import re

# ---------------------------------------------------------- policy gains VolumeOf back
p = 'Jellyfin.Plugin.SubSync/Services/SubSyncService.cs'
s = open(p).read()


def rep(old, new):
    global s
    assert old in s, 'NOT FOUND: ' + old[:130]
    s = s.replace(old, new, 1)


rep("""        /// <summary>
        /// Gets or sets a predicate saying whether a job needs the file's speech analysis
        /// built (embedded extraction or an uncached audio pass). Used only to decide whether
        /// a second job may join the wave for the same file — storage scheduling itself is
        /// left to the OS, which sees the real device queue.
        /// </summary>
        public Func<SyncJob, bool>? IsHeavyIo { get; set; }""",
"""        /// <summary>
        /// Gets or sets a predicate saying whether a job needs the file's speech analysis
        /// built (embedded extraction or an uncached audio pass). Used only to decide whether
        /// a second job may join the wave for the same file — storage scheduling itself is
        /// left to the OS, which sees the real device queue.
        /// </summary>
        public Func<SyncJob, bool>? IsHeavyIo { get; set; }

        /// <summary>
        /// Gets or sets a predicate identifying the storage volume of a job. Used to prefer
        /// spreading a wave over several devices; it never blocks a job from running.
        /// </summary>
        public Func<SyncJob, string>? VolumeOf { get; set; }""")

rep("""            Limit = Math.Clamp(config?.ParallelWorkers ?? 4, 1, 16),
            CanShareMediaFile = job => SpeechIsCached(job),""",
"""            Limit = Math.Clamp(config?.ParallelWorkers ?? 4, 1, 16),
            VolumeOf = job => MediaVolume.Of(_jobContexts.TryGetValue(job.Id, out var c) ? c.Video.Path : null),
            CanShareMediaFile = job => SpeechIsCached(job),""")

open(p, 'w').write(s)
print('policy wired')

# ---------------------------------------------------------- volume helper: new role
p2 = 'Jellyfin.Plugin.SubSync/Services/MediaVolume.cs'
m = open(p2).read()
old_doc = m[m.index('/// <summary>'):m.index('public static class MediaVolume')]
new_doc = """/// <summary>
/// Maps a media path to the storage volume it lives on.
///
/// Used for one thing only: a wave prefers to spread over distinct volumes, because workers
/// reading from one device compete for a single spindle or network link and finish together
/// rather than apart. It is a preference, never a limit — if the queue holds nothing else,
/// a wave still fills up from the same volume.
/// </summary>
"""
m = m.replace(old_doc, new_doc, 1)
open(p2, 'w').write(m)
print('MediaVolume doc updated')
