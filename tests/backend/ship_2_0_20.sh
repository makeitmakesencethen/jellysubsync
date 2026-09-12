#!/bin/bash
# 2.0.19: rigid shift + audio check instead of a widened search, the ordering fix, and retry diagnostics.
# Deploys, runs the fixture (subtitle 120 s out), and publishes only on PASS with clean checks.
set -u
REPO=/opt/data/jellysubsync
JF=/opt/data/jf12test
DLL="$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/Jellyfin.Plugin.SubSync.dll"

export LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu TZ=UTC
cp "$DLL" "$JF/data/plugins/SubSync_2.0.15.0/"
# `dotnet build` does not always leave meta.json beside the dll; the source copy is authoritative.
if [ -f "$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/meta.json" ]; then
  cp "$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/meta.json" "$JF/data/plugins/SubSync_2.0.15.0/"
else
  cp "$REPO/Jellyfin.Plugin.SubSync/meta.json" "$JF/data/plugins/SubSync_2.0.15.0/"
fi
echo "deployed $(md5sum "$JF/data/plugins/SubSync_2.0.15.0/Jellyfin.Plugin.SubSync.dll" | cut -d' ' -f1)"

pkill -f "[j]ellyfin.dll" || true
for i in $(seq 1 60); do
  curl -s -m 4 -o /dev/null http://127.0.0.1:8096/System/Info/Public || break
  sleep 2
done
echo "port released after $i checks"

cd "$REPO/tests/backend" || exit 1
setsid nohup ./start-server.sh >/dev/null 2>&1 &
for i in $(seq 1 90); do
  C=$(curl -s -m 6 -o /dev/null -w "%{http_code}" http://127.0.0.1:8096/System/Info/Public)
  [ "$C" = "200" ] && break
  sleep 4
done
echo "server=$C"
[ "$C" = "200" ] || { echo "ABORT: no server; fixture not run"; exit 1; }

timeout 1800 python3 wide_allowance_fixture.py > /opt/data/tmp/wide_allowance.log 2>&1
echo "fixture exit=$?"
PASSED=$(python3 -c "import json;print(json.load(open('wide_allowance_fixture.json'))['verdict']['pass'])" 2>/dev/null || echo False)
python3 -c "
import json;d=json.load(open('wide_allowance_fixture.json'));v=d['verdict'];r=d['run']
print('verdict:',v);print('run:',{k:r.get(k) for k in ('status','shift_applied_logged','recheck_logged','refused','written','applied_shift_s')})" 2>&1 | tail -3
if [ "$PASSED" != "True" ]; then
  echo "NOT SHIPPING: the fixture did not pass"
  tail -14 /opt/data/tmp/wide_allowance.log
  exit 1
fi

cd "$REPO" || exit 1
python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.18.0</Version>', '<Version>2.0.19.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.18.0"', '"version": "2.0.19.0"'),
    ('build.yaml', 'version: "2.0.18.0"', 'version: "2.0.19.0"'),
):
    p = pathlib.Path(path); t = p.read_text(); assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
old = "## 2.0.18 (beta)"
new = """## 2.0.19 (beta)

A subtitle further out than the offset limit is shifted by the measurement the engine already made, and then checked
against the film's audio. The widened search is gone.

2.0.18 enlarged the search window to 300 s to reach a subtitle two minutes out. On a real file that was wrong: with
the wider window the engine locked onto a **different** part of the audio (56 s where the first pass had measured
94 s), so the subtitle it wrote was wrong from the first line - and the check it was paired with re-ran the *same*
configuration, so it agreed with itself and could not object. A wider window invites a wrong lock; that is the lesson
this release is built on.

- The shift the first pass measured is now applied **rigidly** - pure timestamp arithmetic, nothing to lock onto -
  and that shifted subtitle is aligned against the film's audio with the **normal** allowance. A correct shift leaves
  almost nothing, and the engine's own output is the result; a wrong shift leaves a large residual, and the job
  **refuses and writes nothing** instead of handing over a confidently wrong subtitle.
- `--max-offset-seconds` is never enlarged, which also removes the 300 s value that made one of the retries exit 1.
- Order fixed: the subtitle-reference ceiling (the bad-ruler check) now runs **before** the offset work. In the
  user's log the retry ran first and aligned against a ruler already known to be from a different cut.
- A failed alignment run now reports what the engine said (its last lines), not just the exit code.

## 2.0.18 (beta)"""
assert old in s
c.write_text(s.replace(old, new, 1))
print('2.0.19 prepared')
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
2.0.19: the measured shift applied rigidly and checked against the audio, instead of a widened search

2.0.18 widened the engine's search to 300 s so a subtitle two minutes out could be reached. On the user's file that
produced a wrong result: the wider window let the engine lock onto a different part of the audio - 56 s where the
first pass had measured 94 s - so the subtitle was wrong from the first line. The check paired with it re-ran the
same configuration and therefore agreed with itself. Widening a search invites a wrong lock.

This release applies the shift the first pass measured rigidly (pure timestamp arithmetic, nothing to lock onto) and
then aligns that against the film's audio with the normal allowance: a correct shift leaves almost nothing and the
engine's own output is the result, a wrong shift leaves a large residual and the job refuses rather than writing a
confidently wrong subtitle. --max-offset-seconds is never enlarged, which also removes the 300 s value that made one
of the user's retries exit 1.

Two more fixes from the same log: the subtitle-reference ceiling (bad-ruler check) now runs before the offset work,
because the retry had been aligning against a ruler already known to be from a different cut; and a failed alignment
run reports the engine's last lines instead of only the exit code.

Checks: all passing.
EOF
