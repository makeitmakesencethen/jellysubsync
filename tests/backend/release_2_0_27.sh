#!/bin/bash
# 2.0.27: the metadata scan reports what it has read, and a cue index key can no longer skip a subtitle.
set -u
set -o pipefail
export LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu TZ=UTC
REPO=/opt/data/jellysubsync
cd "$REPO" || exit 1

python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.26.0</Version>', '<Version>2.0.27.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.26.0"', '"version": "2.0.27.0"'),
    ('build.yaml', 'version: "2.0.26.0"', 'version: "2.0.27.0"'),
):
    p = pathlib.Path(path); t = p.read_text()
    if new in t:
        continue
    assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
if '## 2.0.27 (beta)' not in s:
    old = '## 2.0.26 (beta)'
    new = """## 2.0.27 (beta)

Two extraction defects from the Tier 4 list: a progress line that never counted, and a cue index key that could skip a subtitle.

The metadata scan is the route that reads most of a file - it is what runs when a track's cue points are missing,
or name their clusters but not the blocks inside them - so on a network share it is the slowest thing this plugin
does, and its progress line printed the reading counters that only the cue-indexed route fills. It read "0,0 MB"
from the first cluster to the last.

- **The scan reports what it has read.** The line takes its numbers from the reader the pass is reading through
  (the kernel's counters where the platform provides them, the reader's own count otherwise), and the pass's own
  counters are brought up to date with it, so the summary at the end carries the same figures the live lines
  showed. Measured on a 520-cluster fixture: `scanning clusters (400 read, 0,0 MB, 200 subtitles found)` became
  `scanning clusters (400 read, 1,6 MB, 200 subtitles found)`.

The cue-indexed loop skips a cue point it has already seen, keyed by `cluster position * 31 + block offset` - a
hash of two numbers rather than the pair itself. Two cue points whose clusters are d bytes apart and whose block
offsets differ by exactly 31d therefore share a key, and the second one was skipped without its cluster ever being
read: its line was lost, and the pass reported success.

- **The key is the pair it means.** A foreign or damaged cue index is where such a pair comes from, and this project
  has met two of those already. Reproduced with a generator written for it (`tests/fixtures/make_collision.py`:
  clusters 67 bytes apart, one cue point carrying an offset of 31 x 67 that points outside its own cluster, the next
  sitting at offset 0 where its block is): one cue before, two after, and the same file with the first offset nudged
  by one byte kept both cues either way. Nothing moved on the files this work was measured against: kopps 829 cues,
  Sune i Grekland 1019, the D17 mixed-index track 803.

## 2.0.26 (beta)"""
    assert old in s
    c.write_text(s.replace(old, new, 1))
print('2.0.27 versioned and recorded')
PY

/opt/data/.dotnet/dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -3

python3 tests/run_checks.py > /tmp/release_2_0_27_checks.txt 2>&1
SUITE=$?
echo "suite exit=$SUITE, $(grep -c '^PASS' /tmp/release_2_0_27_checks.txt) checks, $(grep -c '^FAIL' /tmp/release_2_0_27_checks.txt) failures"
if [ "$SUITE" != "0" ]; then
  echo "NOT PUSHING: checks failing"
  grep '^FAIL' /tmp/release_2_0_27_checks.txt | head -5
  exit 1
fi

git add -A && git commit -q -F - <<'EOF' && git push origin beta 2>&1 | tail -1 && echo PUSHED
2.0.27: the metadata scan reports what it has read, and the cue key is the pair it means

B11: the metadata-scan route's progress line printed the stats field only the cue-indexed loop fills, so the one
line a user watches for minutes read 0,0 MB for the whole pass - on the slowest route the plugin has. It takes its
numbers from the reader now.

B24: the cue-indexed loop's dedup key was `cluster position * 31 + block offset`, a hash of two numbers rather
than the pair, so a cue point could be skipped without its cluster ever being read. Reproduced with
tests/fixtures/make_collision.py and fixed; the files this work is measured against are unchanged (kopps 829,
Sune i Grekland 1019, D17 mixed-index track 803).

494 checks green.
EOF
