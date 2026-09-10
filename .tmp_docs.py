import re

# ---------------------------------------------------------------- knowledge doc
p = '/opt/data/jellysubsync/knowledge/architecture-and-gotchas.md'
t = open(p).read()
start = t.find("- Waves are bounded by `ParallelWorkers` **alone**")
assert start > 0
end = t.find("\n\n", t.find("why is parallel running one file at a time?\"", start))
if end < 0:
    end = t.find("\n\n", start)
new = """- Waves are bounded by `ParallelWorkers` and nothing else — there is no per-volume budget.
  Volume information (`VolumeOf` → `MediaVolume.Of`) only expresses a *preference*: the first
  selection pass takes the first job of each distinct volume so a wave spreads over the
  storage you have, and a second pass then fills the remaining slots with whatever is left,
  same volume or not. A queue that lives on one disk therefore still runs at full width."""
t = t[:start] + new + t[end:]
open(p, 'w').write(t)
print('knowledge updated')

# ---------------------------------------------------------------- README
p = '/opt/data/jellysubsync/README.md'
r = open(p).read()
anchor = "## Features"
assert anchor in r
bullet = """- **Waves spread over your disks** — when a batch spans several volumes, each wave picks one
  file per volume before doubling up, since workers reading the same device finish together
  rather than apart. This is a preference, not a limit: a library on a single disk still runs
  at the full worker count. Several subtitles of the *same* file never start before that
  file's audio analysis is cached, so no two workers race to build it.
"""
r = r.replace(anchor, anchor + "\n" + bullet, 1)
open(p, 'w').write(r)
print('README bullet added')

# ---------------------------------------------------------------- changelog
p = '/opt/data/jellysubsync/CHANGELOG.md'
c = open(p).read()
entry = """## [1.1.0.21]

### Changed
- Parallel waves now **spread across storage volumes as a preference**: the scheduler takes
  one file per volume first, then fills the remaining worker slots with whatever is left,
  same disk or not. A batch that spans several disks runs one file from each instead of
  hammering one; a batch that lives on a single disk still runs at the full worker count,
  because nothing is blocked. Volumes never cap a wave — they only decide which job goes in
  first. The rule that several subtitles of the same media file never start before that
  file's audio analysis is cached is unchanged.

"""
idx = c.find("## [1.1.0.20]")
assert idx > 0
c = c[:idx] + entry + c[idx:]
open(p, 'w').write(c)
print('changelog entry added')

# ---------------------------------------------------------------- version bump
p = '/opt/data/jellysubsync/Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj'
x = open(p).read()
x = x.replace('<Version>1.1.0.20</Version>', '<Version>1.1.0.21</Version>')
open(p, 'w').write(x)
print('version bumped')
