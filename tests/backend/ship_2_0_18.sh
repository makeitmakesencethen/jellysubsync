#!/bin/bash
# Deploy the current build, run the wide-allowance fixture, and ship 2.0.18 only if it passes with clean checks.
set -u
REPO=/opt/data/jellysubsync
JF=/opt/data/jf12test
DLL="$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/Jellyfin.Plugin.SubSync.dll"

cp "$DLL" "$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/meta.json" "$JF/data/plugins/SubSync_2.0.15.0/"
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
print('verdict:',v);print('run:',{k:r.get(k) for k in ('status','retry_logged','recheck_logged','refused','written','median_offset_vs_reference_s')})" 2>&1 | tail -3
if [ "$PASSED" != "True" ]; then
  echo "NOT SHIPPING: the fixture did not pass"
  tail -14 /opt/data/tmp/wide_allowance.log
  exit 1
fi

cd "$REPO" || exit 1
python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.17.0</Version>', '<Version>2.0.18.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.17.0"', '"version": "2.0.18.0"'),
    ('build.yaml', 'version: "2.0.17.0"', 'version: "2.0.18.0"'),
):
    p = pathlib.Path(path); t = p.read_text(); assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
old = "## 2.0.17 (beta)"
new = """## 2.0.18 (beta)

A subtitle further out than the offset limit gets one wide retry, checked against the audio again.

"Maximum offset" (60 s by default) exists because a result pinned to it is the most the engine was allowed to apply,
not what the file needed. Refusing is safe and unhelpful: subtitles two minutes out are real, and the job used to end
there with nothing written.

- A result at or past the limit now sends that subtitle through **one more alignment with a wide allowance** - four
  times the configured ceiling, at least 300 s - instead of ending the job.
- The wide result is then **checked again with a tight allowance** against the film's own audio, the same double-check
  a framerate stretch gets. If the film agrees (only seconds left to fix) the result is written; if the wide pass
  locked onto the wrong part of the audio, the tight pass still asks for a large shift, and the job refuses with the
  numbers, writing nothing.
- The message is honest now: a 94 s shift is reported as "at or past the 60 s ceiling it was allowed", not as
  "sitting on" it.
- The hold-predicate behind both checks is one function (`AlignmentHoldsAgainstAudio`), so the stretch check and the
  wide-retry check cannot drift apart.

Verified by `tests/backend/wide_allowance_fixture.py`: the fixture's subtitle is the file's own track 120 s out - past
the 30 s a subtitle reference is trusted for, past the 60 s offset ceiling, inside the 300 s retry - and the run
completes with the written subtitle within seconds of the film's own track, after the log shows the retry and the
second check.

## 2.0.17 (beta)"""
assert old in s
c.write_text(s.replace(old, new, 1))
print('2.0.18 prepared')
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
2.0.18: a subtitle past the offset limit gets one wide retry, checked against the audio again

"Maximum offset" bounds what the engine may apply, so a result sitting at or past it is the most it was allowed to
do, not what the file needed - and the job used to end there with nothing written. Subtitles two minutes out are
real (the user's Clara Sola files need 94 s and 99 s against a 60 s limit), so that subtitle now goes through one
more alignment with a wide allowance: four times the configured ceiling, at least 300 s.

The wide result is then checked again with a tight allowance against the film's own audio - the same double-check a
framerate stretch gets - so a wide allowance cannot smuggle in a lock onto the wrong part of the audio. If the film
agrees, the result is written; if it does not, the job refuses with the numbers and writes nothing.

Both checks share one predicate (AlignmentHoldsAgainstAudio, renamed from StretchHoldsAgainstAudio) so they cannot
drift apart. The refusal message no longer calls a 94 s shift "sitting on" a 60 s ceiling.

Verified by tests/backend/wide_allowance_fixture.py: a subtitle 120 s out - past the subtitle-reference trust limit
(30 s), past the offset ceiling (60 s), inside the retry (300 s) - completes with the written subtitle within seconds
of the film's own track, with the log showing the retry and the second check.

Checks: all passing.
EOF
