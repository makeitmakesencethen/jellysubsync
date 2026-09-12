#!/bin/bash
# 2.0.22: the cue window adapts from the reads the pass actually makes, because a probe taken before the
# work cannot size it (it swung 0,4 ms - 255 ms per 16 KB on the same box within minutes).
set -u
set -o pipefail
export LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu TZ=UTC
REPO=/opt/data/jellysubsync
cd "$REPO" || exit 1

python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.21.0</Version>', '<Version>2.0.22.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.21.0"', '"version": "2.0.22.0"'),
    ('build.yaml', 'version: "2.0.21.0"', 'version: "2.0.22.0"'),
):
    p = pathlib.Path(path); t = p.read_text()
    if new in t:
        continue  # already bumped by an earlier attempt in this session
    assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
if '## 2.0.22 (beta)' not in s:
    old = '## 2.0.21 (beta)'
    new = """## 2.0.22 (beta)

The cue window now adapts while the pass runs, because a probe taken before the work cannot size it.

2.0.21 sized the cue read window from a probe of the storage, and on the storage it was built for it changed
nothing: the rule refused to grow the window unless a 1 MB read cost about what a 16 KB read costs, and on
the share in question it does not. The log from that machine shows what that leaves - 1500 reads per pass at
6 KB per read, 46-114 s per file, against 0,3-3 s on its faster volumes.

Two more things were wrong with deciding this before the work starts. The probe's own numbers swing by two
orders of magnitude while other jobs and lanes load the same disk (0,4 ms and 255 ms per 16 KB on one box
within minutes), and taking it costs a 1 MB read per file.

- **The pass times its own reads and adapts.** It starts at 4 KB; if the reads it is actually making average
  4 ms or more, the window quadruples (bounded at 1 MB); if they average 0,5 ms or less, it quarters (bounded
  at 4 KB). Every change is logged, so the log shows the pass learning its storage.
- **The pre-work 1 MB probe read is gone**, and with it the ratio gate that made 2.0.21 inert there.
- A pass on cheap storage still reads a small fraction of the file: that guard is unchanged, and it now says
  which of the two regimes it is checking.

## 2.0.21 (beta)"""
    assert old in s
    c.write_text(s.replace(old, new, 1))
print('2.0.22 versioned and recorded')
PY

/opt/data/.dotnet/dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -3

# The suite's exit code is its verdict (it raises SystemExit with the failure count).
python3 tests/run_checks.py 2>&1 | tail -4
FAILS=${PIPESTATUS[0]}
echo "suite exit=$FAILS"
if [ "$FAILS" != "0" ]; then
  echo "NOT PUSHING: checks failing"
  exit 1
fi

git add -A && git commit -q -F - <<'EOF' && git push origin beta 2>&1 | tail -1 && echo PUSHED
2.0.22: the cue window adapts from the reads the pass actually makes

2.0.21 sized the cue window from a pre-work probe, and on the storage it was written for it changed nothing:
the ratio gate refused to grow the window unless a 1 MB read cost about what a 16 KB read costs, which on
that share it does not. Its log shows the result - 1500 reads per pass at ~6 KB per read, 46-114 s per file,
against 0,3-3 s on the same machine's faster volumes.

A pre-work probe is the wrong instrument here anyway: while other jobs and lanes load the same disk its
numbers swing by two orders of magnitude (0,4 ms and 255 ms per 16 KB within minutes on one box), and it
costs a 1 MB read per file to take.

- The pass times the reads it is really making: starting at 4 KB, quadrupling the window when they average
  4 ms or more (bounded at 1 MB) and quartering it when they average 0,5 ms or less (bounded at 4 KB). Each
  change is logged.
- The pre-work 1 MB probe read and the ratio gate are gone.
- The fraction-of-the-file guard stays, and now states which regime it is checking.

All checks passing.
EOF
