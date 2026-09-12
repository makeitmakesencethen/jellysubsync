#!/bin/bash
# 2.0.21: how much to read at once is a property of the storage, and the cue-indexed path had it
# hard-coded to 4 KB - one read per cue, which on a share that charges per round trip is the whole
# cost of the extraction. The probe now measures both halves of that cost.
set -u
export LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu TZ=UTC
REPO=/opt/data/jellysubsync
cd "$REPO" || exit 1

python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.20.0</Version>', '<Version>2.0.21.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.20.0"', '"version": "2.0.21.0"'),
    ('build.yaml', 'version: "2.0.20.0"', 'version: "2.0.21.0"'),
):
    p = pathlib.Path(path); t = p.read_text()
    if new in t:
        continue  # already bumped by an earlier attempt in this session
    assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
if '## 2.0.21 (beta)' not in s:
    old = '## 2.0.20 (beta)'
    new = """## 2.0.21 (beta)

Extraction stops paying for one round trip per subtitle cue.

A cue-indexed file reads one small piece per cue, and that window was hard-coded to 4 KB. On a share that
charges per round trip - the log shows 29,84 ms per 16 KB read - a file with 850 cues spent ~12 s of pure
waiting on reads that carried 5 MB in total, and a pass over one file measured 21 s while the sync work
that followed it took 1,5-2 s.

- **The window is now sized from the storage, both halves of it.** The probe already timed a small read;
  it now also times a 1 MB read, so the window becomes the bytes one round trip can carry (the
  bandwidth-delay product, clamped to 4 KB … 1 MB). A share that charges ~13 ms per round trip carries a
  megabyte in one, so its cues come back a few dozen reads at a time instead of one read each; local
  storage where a read is free stays at 4 KB and reads no more than before.
- **The probe line says what it measured**, so the choice is visible in the log rather than implied:
  ms per 16 KB read, KB per round trip, and both chosen windows.
- **Nothing else about a pass changed**: the multi-track pass keeps its bounded window, the scan window
  keeps its own rule, and the extracted subtitles are the same bytes.

Worth saying plainly, since it looked like a worker shortage: three extraction lanes were already running
in the batch that prompted this (the log shows all three starting), and the syncs between them were 1,5-2 s
each. One job ran at a time because a file's job cannot start until that file's pass finishes - the pass
was the queue.

## 2.0.20 (beta)"""
    assert old in s
    c.write_text(s.replace(old, new, 1))
print('2.0.21 versioned and recorded')
PY

/opt/data/.dotnet/dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -3
# The suite's exit code is its verdict (it raises SystemExit with the failure count); its last lines
# are printed for the record. Parsing a "N failing" line was wrong - no such line exists, so the gate
# refused to push even when the suite was green.
python3 tests/run_checks.py 2>&1 | tail -4
FAILS=$?
if [ "$FAILS" != "0" ]; then
  echo "NOT PUSHING: checks failing"
  exit 1
fi

git add -A && git commit -q -F - <<'EOF' && git push origin beta 2>&1 | tail -1 && echo PUSHED
2.0.21: size the cue-read window from the storage instead of hard-coding 4 KB

The cue-indexed path read 4 KB per cue, so a share that charges per round trip paid one round trip per
cue: 784 reads carrying 5.4 MB in a 21 s pass, against 1.5-2 s of actual sync work. Which is cheaper -
many small reads or fewer big ones - is a property of the storage, and the probe already measured half of
it. It now measures the other half too (a 1 MB read, timed end to end), and the cue window becomes the
bytes one round trip can carry, clamped to 4 KB..1 MB. Fast local storage is unchanged; the share that
charges ~13 ms per round trip gets its cues a few dozen reads at a time.

The probe line now prints ms per 16 KB read, KB per round trip, and both windows, so the decision is
visible in the log rather than implied by it.

This also corrects a diagnosis: the batch that prompted it had three extraction lanes running already, and
its syncs were 1.5-2 s each. Jobs ran one at a time because a file's job waits for that file's pass, so
the pass - not the worker count - was the queue.

All checks passing.
EOF
