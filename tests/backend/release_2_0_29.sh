#!/bin/bash
# 2.0.29: the extraction summary's own figures are the pass's figures (B10).
set -u
set -o pipefail
export LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu TZ=UTC
REPO=/opt/data/jellysubsync
cd "$REPO" || exit 1

python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.28.0</Version>', '<Version>2.0.29.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.28.0"', '"version": "2.0.29.0"'),
    ('build.yaml', 'version: "2.0.28.0"', 'version: "2.0.29.0"'),
):
    p = pathlib.Path(path); t = p.read_text()
    if new in t:
        continue
    assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
if '## 2.0.29 (beta)' not in s:
    old = '## 2.0.28 (beta)'
    new = """## 2.0.29 (beta)

The extraction summary's own figures are now the figures the pass finished with.

`FinaliseStats` derives average ms/read and blocks/s from the counters a pass ends with, and both call sites
assigned those counters *after* calling it - so the block rate was 0 on every extraction that read a file. The
ms/read figure came out right only by accident: the cue-indexed loop's live counter line fills its half as it
goes, which is why nothing you read in the log ever showed the zero.

- Reproduced before touching the code, on all four fixture shapes: `blocks=10 totalMs=0.8 perSecond=0.00`.
- Fixed by assigning the counters first, at both the success and the failure call site. The check now asserts the
  figure equals `SubtitleBlocks / (TotalMs / 1000)` exactly rather than merely being non-zero - after the fix,
  15 908 / 8 221 / 14 120 / 10 137 blocks/s on those same fixtures.
- The summary's text recomputed blocks/s itself as a workaround for the zero. That second copy is gone, because
  it produced the same number from the same total time: nothing you read in the log changes.

No change to a sync itself - the extraction route, the reads it makes and the file it writes are untouched.

## 2.0.28 (beta)"""
    assert old in s
    c.write_text(s.replace(old, new, 1))
print('2.0.29 versioned and recorded')
PY

/opt/data/.dotnet/dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -3

python3 tests/run_checks.py > /tmp/release_2_0_29_checks.txt 2>&1
SUITE=$?
echo "suite exit=$SUITE, $(grep -c '^PASS' /tmp/release_2_0_29_checks.txt) checks, $(grep -c '^FAIL' /tmp/release_2_0_29_checks.txt) failures"
if [ "$SUITE" != "0" ]; then
  echo "NOT PUSHING: checks failing"
  grep '^FAIL' /tmp/release_2_0_29_checks.txt | head -5
  exit 1
fi

git add -A && git commit -q -F - <<'EOF' && git push origin beta 2>&1 | tail -1 && echo PUSHED
2.0.29: the extraction summary's block rate is the pass's own figure

B10. `FinaliseStats` derives ms/read and blocks/s from the counters the pass ends with, and both call sites
assigned those counters after calling it, so the block rate was 0 on every extraction that read a file.
ReadLatencyMs survived by accident - the cue-indexed loop's live-counter line fills ReadCalls as it goes - which
is why the log lines this project reads runs out of never showed the zero.

Reproduced first, on the four fixtures (cue-indexed, walk, grouped-cue, multi-track): 4 failures reading
"blocks=10 totalMs=0.8 perSecond=0.00". After assigning the counters before FinaliseStats at both sites the same
field carries the pass's own figure (15908.37 / 8221.66 / 14120.30 / 10137.88 blocks/s) and the check asserts it
equals SubtitleBlocks / (TotalMs / 1000) exactly rather than merely being non-zero. The recomputed blocks/s
fallback in ToString goes with it - it produced the same number from the same total time, so no log text moves.

Also in this release: FIX_PLAN records for B10 (fixed, with the field evidence that named it corrected - the
plan line's ms is a Price() estimate, not a measured time) and B31 (latent, low: an unguarded scheduler loop and
lane failures that only reach Jellyfin's log; the 2.0.28 batch it was opened for finished cleanly).

513 checks green.
EOF
