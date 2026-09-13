#!/bin/bash
# 2.0.26: a cue index's offsets belong to the track they name, and each block is one cue.
set -u
set -o pipefail
export LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu TZ=UTC
REPO=/opt/data/jellysubsync
cd "$REPO" || exit 1

python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.25.0</Version>', '<Version>2.0.26.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.25.0"', '"version": "2.0.26.0"'),
    ('build.yaml', 'version: "2.0.25.0"', 'version: "2.0.26.0"'),
):
    p = pathlib.Path(path); t = p.read_text()
    if new in t:
        continue
    assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
if '## 2.0.26 (beta)' not in s:
    old = '## 2.0.25 (beta)'
    new = """## 2.0.26 (beta)

A cue index's offsets now belong to the track they name, and a block is one subtitle however many routes reach it.

One cue point carries one CueTrackPositions per track: mkvmerge writes the video's position and every
subtitle track's position into the same cue point, the highest track number last. The parser read the
cluster and the block offset into variables that outlived each CueTrackPositions and adopted whichever came
last, so a cue point that matched the track being extracted still handed it a neighbouring track's offsets.
On one 9,4 GB film 104 of 829 cue points named another track's block; on a 4,9 GB one, all 1019 did. Every
one of those fell back to walking its cluster to find the block, and a walk cannot know which blocks a
neighbouring cue point already emitted - so one subtitle came out twice, once carrying its own duration and
once the two-second default this extractor uses when a block has none, which the writer's overlap pass then
cut to 1,5 s. Two copies of the same line, with different end times.

- **The offsets are attributed to the track they belong to.** The index locates the block it names, so the
  walks stop happening. Measured against the file's own cue index and against ffmpeg: the 9,4 GB film went
  from 832 cues to 829 where the index and ffmpeg both say 829, and the 4,9 GB one from 1251 to 1019 where
  both say 1019. Cluster visits fell from 933 to 829 and from 2038 to 1019.
- **A block is one subtitle however many routes reach it.** Where an index locates only some of a track's
  cue points, the walk over a cluster used to emit a block the cue point beside it had already emitted. A
  pass now remembers the blocks it has emitted, so the mixed-index fixture yields 803 cues instead of 843 -
  and 803 is what ffmpeg reports for it.
- **The cluster count says one visit per cluster.** A walked cluster was counted twice, once by the cue
  point that named it and once by the walk itself, so the log read 15 visits for 10 cue points and 1205 for
  803 cue points. It reads like the reference extraction now.

## 2.0.25 (beta)"""
    assert old in s
    c.write_text(s.replace(old, new, 1))
print('2.0.26 versioned and recorded')
PY

/opt/data/.dotnet/dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -3

python3 tests/run_checks.py > /tmp/release_checks.txt 2>&1
SUITE=$?
echo "suite exit=$SUITE, $(grep -c '^PASS' /tmp/release_checks.txt) checks, $(grep -c '^FAIL' /tmp/release_checks.txt) failures"
if [ "$SUITE" != "0" ]; then
  echo "NOT PUSHING: checks failing"
  grep '^FAIL' /tmp/release_checks.txt | head -5
  exit 1
fi

git add -A && git commit -q -F - <<'EOF' && git push origin beta 2>&1 | tail -1 && echo PUSHED
2.0.26: cue-index offsets belong to the track they name, and a block is one cue

A cue point carries one CueTrackPositions per track, and the parser kept the cluster and block offsets in
variables that outlived each of them - so the last one won, and a cue point matching the wanted track was
handed a neighbouring track's offsets. On kopps 104 of 829 cue points named another track's block; on Sune
i Grekland all 1019 did. Each one fell back to walking its cluster, and a walk re-emitted blocks a
neighbouring cue point had already emitted: the same subtitle twice, once with its own duration and once
with the two-second default.

Fixing the attribution takes kopps to 829 cues and Sune i Grekland to 1019, which is what the files' own
indexes and ffmpeg both report (from 832 and 1251), and remembering emitted blocks takes the mixed-index
fixture to 803 (ffmpeg's number, from 843).

ClustersVisited counted a walked cluster twice, once in the cue loop and once in the walk, so it read 15
visits for 10 cue points and 1205 for 803. It is one visit per cluster now, matching the reference.

493 checks green.
EOF
