#!/bin/bash
# 2.0.23: the prefetched ranges must include the cluster head, or the pass reads the file anyway.
set -u
set -o pipefail
export LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu TZ=UTC
REPO=/opt/data/jellysubsync
cd "$REPO" || exit 1

python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.22.0</Version>', '<Version>2.0.23.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.22.0"', '"version": "2.0.23.0"'),
    ('build.yaml', 'version: "2.0.22.0"', 'version: "2.0.23.0"'),
):
    p = pathlib.Path(path); t = p.read_text()
    if new in t:
        continue
    assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
if '## 2.0.23 (beta)' not in s:
    old = '## 2.0.22 (beta)'
    new = """## 2.0.23 (beta)

The prefetched ranges now start at the cluster, so the pass finally reads from memory.

The per-cue loop reads a cluster's header first (to find where its children begin) and only then the block
the index named. The prefetch built its ranges from the block position - or from the first block's relative
offset, which is relative to the cluster's *data* start - so that first read fell outside every range and
went to disk. The fetch was made and then ignored: one file fetched 118,3 MB and the loop still issued 2 282
real reads, 70,9 s of which was waiting on the same share that had just answered the prefetch.

- **Both prefetches cover the cluster head now**: the cue-indexed path and the shared multi-track pass.
  The reads the fetch was made for are served from it, so the loop stops issuing one real read per cue.
- **What this does not fix**: a file whose cue index does not locate its subtitle blocks has to walk the
  clusters, and walking reads about the file - on a share delivering ~11 MB/s that is ~2 minutes for a
  1,3 GB remux however the reads are arranged. Locating those blocks from the video track's own index
  instead of walking is the next piece of work; it is measured, not guessed, and is not in this release.

## 2.0.22 (beta)"""
    assert old in s
    c.write_text(s.replace(old, new, 1))
print('2.0.23 versioned and recorded')
PY

/opt/data/.dotnet/dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -3

python3 tests/run_checks.py 2>&1 | tail -4
FAILS=${PIPESTATUS[0]}
echo "suite exit=$FAILS"
if [ "$FAILS" != "0" ]; then
  echo "NOT PUSHING: checks failing"
  exit 1
fi

git add -A && git commit -q -F - <<'EOF' && git push origin beta 2>&1 | tail -1 && echo PUSHED
2.0.23: the prefetched ranges must include the cluster head, or the pass reads the file anyway

The per-cue loop reads the cluster header before the block the index names, and the prefetch built its
ranges from the block (or from a relative offset measured against the cluster's data start), so that first
read fell outside every range and went to disk. The fetch was made and then ignored: one file fetched
118,3 MB while the loop still issued 2 282 real reads and spent 70,9 s waiting on the share that had just
answered the prefetch.

- The cue-indexed path and the shared multi-track pass both start their ranges at the cluster now, so the
  reads they were fetched for are served from memory.

Not fixed here, and measured: files whose cue index does not locate their subtitle blocks must walk the
clusters, which reads about the file itself - ~2 minutes for a 1,3 GB remux on a share delivering ~11 MB/s,
whatever the read pattern. Locating those blocks from the video track's own index is the next piece.

All checks passing.
EOF
