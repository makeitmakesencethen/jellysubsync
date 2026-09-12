#!/bin/bash
# Fix the falsy-zero in the bad-reference fixture's own verdict, re-run it, and if it passes, ship 2.0.17.
#
# Two fixtures have now reported CHECK for their best possible outcome because `(value or default)` treats a
# legitimate 0.0 as missing. None-checks, and a hard stop before anything is published unless the fixture passes.
set -u
REPO=/opt/data/jellysubsync
cd "$REPO/tests/backend" || exit 1

python3 - <<'PY'
import pathlib
p = pathlib.Path('bad_reference_fallback.py')
t = p.read_text()
old = """    ok = (run.get("audio_fallback_logged") and not run.get("refused")
          and run.get("status") == "Completed" and run.get("written")
          and (run.get("median_offset_vs_reference_s") or 99) <= 5.0)"""
new = """    # None-checks, not `or` defaults: a perfect result is an offset of 0.0 s, and `or 99` reads that as missing.
    offset = run.get("median_offset_vs_reference_s")
    ok = bool(run.get("audio_fallback_logged") and not run.get("refused")
              and run.get("status") == "Completed" and run.get("written")
              and offset is not None and offset <= 5.0)"""
assert old in t, 'verdict block not found'
p.write_text(t.replace(old, new, 1))
print('fixture verdict: None-checks instead of falsy zero')
PY

timeout 1500 python3 bad_reference_fallback.py > /opt/data/tmp/bad_reference_fallback.log 2>&1
echo "fixture exit=$?"
PASSED=$(python3 -c "import json;print(json.load(open('bad_reference_fallback.json'))['verdict']['pass'])")
python3 -c "
import json;d=json.load(open('bad_reference_fallback.json'));print('verdict:',d['verdict']);print('run:',{k:d['run'].get(k) for k in ('status','audio_fallback_logged','refused','written','median_offset_vs_reference_s')})"

if [ "$PASSED" != "True" ]; then
  echo "NOT SHIPPING: the fixture did not pass"
  exit 1
fi

cd "$REPO" || exit 1
python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.16.0</Version>', '<Version>2.0.17.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.16.0"', '"version": "2.0.17.0"'),
    ('build.yaml', 'version: "2.0.16.0"', 'version: "2.0.17.0"'),
):
    p = pathlib.Path(path); t = p.read_text(); assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
old = "## 2.0.16 (beta)"
new = """## 2.0.17 (beta)

A subtitle reference that is not the same cut is replaced by the audio, instead of ending the job.

A file's own subtitle track is a free and exact ruler - 265 ms and 8.7 MB to read, against minutes of audio analysis
- so it is used when it exists. When it is from a different cut, the alignment it produces is nonsense: on the file
this came from, the embedded track demanded a 111.9 s shift of the user's subtitle. That is what the
`MaxSubtitleReferenceOffsetSeconds` ceiling (30 s by default) exists to catch, and until now it refused the subtitle.

- The ceiling still governs what a subtitle reference is trusted for. Past it, the track is now **discarded as a
  ruler** - so the file's other subtitles do not repeat the same measurement - and the subtitle is aligned against
  the **audio** instead, which cannot be a wrong cut. The result is written.
- Cost lands only on files with a bad reference: one audio analysis per file, cached like every other audio path, and
  a log line saying so. Files whose reference is fine are untouched.
- The outcome says it plainly: "the file's own subtitle track is not the same cut, so this was aligned against the
  audio".
- If the audio alignment after a bad reference produces nothing, the job still refuses and writes nothing.
- **Contract change**: `MaxSubtitleReferenceOffsetSeconds` was documented as a refusal; it is now the trigger for the
  audio alignment. AGENTS.md is updated with it.

Verified with `tests/backend/bad_reference_fallback.py`: the fixture's subtitle is the file's own track 45 s out -
past the 30 s a subtitle reference is trusted for and inside the audio path's 60 s - and the run completes with the
written subtitle 0.0 s from the film's own track, where before it refused.

## 2.0.16 (beta)"""
assert old in s
c.write_text(s.replace(old, new, 1))
print('2.0.17 prepared')
PY

dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -3
PASSES=$(python3 tests/run_checks.py 2>&1 | grep -cE '^PASS')
FAILS=$(python3 tests/run_checks.py 2>&1 | grep -cE '^FAIL')
echo "checks: $PASSES passing, $FAILS failing"
if [ "$FAILS" != "0" ]; then
  echo "NOT SHIPPING: checks failing"
  exit 1
fi

git add -A && git commit -q -F - <<'EOF' && git push origin beta 2>&1 | tail -1 && echo PUSHED
2.0.17: a subtitle reference that is not the same cut is replaced by the audio

A file's own subtitle track is a free exact ruler, so it is used when it exists - and when it is from a different cut
the alignment it produces is nonsense: on the user's file the embedded track demanded a 111.9 s shift of the
subtitle. MaxSubtitleReferenceOffsetSeconds caught that and refused the subtitle, which is where the job ended.

Past the ceiling the track is now discarded as a ruler - so the file's other subtitles do not repeat the same
measurement - and the subtitle is aligned against the audio instead, which cannot be a wrong cut. The result is
written. The cost falls only on files with a bad reference: one audio analysis per file, cached like every other
audio path. If that alignment produces nothing, the job refuses and writes nothing as before.

Contract change, documented in AGENTS.md: the ceiling is no longer a refusal, it is the trigger for the audio
alignment.

Verified by tests/backend/bad_reference_fallback.py - a subtitle that is the file's own track 45 s out (past the 30 s
a subtitle reference is trusted for, inside the audio path's 60 s) completes with the written subtitle 0.0 s from the
film's own track, where it used to refuse.

Checks: all passing.
EOF
