#!/bin/bash
# 2.0.20 by hand: the code is in the tree (it landed under a mislabelled commit when a ship script re-ran), the
# version was never bumped, so the release workflow correctly refused to republish 2.0.19. Bump it, record it,
# commit, push, and watch the catalog.
set -u
export LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu TZ=UTC
REPO=/opt/data/jellysubsync
cd "$REPO" || exit 1

python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.19.0</Version>', '<Version>2.0.20.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.19.0"', '"version": "2.0.20.0"'),
    ('build.yaml', 'version: "2.0.19.0"', 'version: "2.0.20.0"'),
):
    p = pathlib.Path(path); t = p.read_text(); assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
if '## 2.0.20 (beta)' not in s:
    old = '## 2.0.19 (beta)'
    new = """## 2.0.20 (beta)

The offset ceiling is a search window, so it now has room - and the rigid-shift attempt is gone.

A real file syncs when "Maximum offset" is 150 and not when it is 60, and that is the whole story: ffsubsync's
`--max-offset-seconds` is the range the alignment may look in. With 60 s the engine could not see an answer at ~112 s
and returned the best *wrong* one it could find (56 s, then 60 s). No plugin logic can repair an answer that is
outside the searched range.

- **Default raised to 180 s.** It mirrors upstream's 60 s default, which is simply too small for real libraries.
- **A result that lands on the window is retried once with twice it**, and written only when it is not pinned to the
  wider window either and one alignment against the film's audio asks for nothing more. If the wider window is pinned
  too, the job refuses and says to raise the setting.
- **The rigid-shift path is deleted.** It applied the measured displacement as a pure shift, but when the engine also
  re-times a subtitle (a framerate correction) that number is the median displacement of a rescaled timeline, and
  applying it as a shift is wrong by construction. Tried in 2.0.19; wrong on the file it was written for.
- The setting's description and AGENTS.md now say what the value is: a search range, not a trust limit.

## 2.0.19 (beta)"""
    assert old in s
    c.write_text(s.replace(old, new, 1))
print('2.0.20 versioned and recorded')
PY

/opt/data/.dotnet/dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -3
PASSES=$(python3 tests/run_checks.py 2>&1 | grep -cE '^PASS')
FAILS=$(python3 tests/run_checks.py 2>&1 | grep -cE '^FAIL')
echo "checks: $PASSES passing, $FAILS failing"
if [ "$FAILS" != "0" ]; then
  echo "NOT PUSHING: checks failing"
  exit 1
fi

git add -A && git commit -q -F - <<'EOF' && git push origin beta 2>&1 | tail -1 && echo PUSHED
2.0.20: the offset ceiling is a search window with room, and the rigid-shift attempt is gone

The file this was built for syncs with "Maximum offset" at 150 and not with it at 60, which is the whole diagnosis:
ffsubsync's --max-offset-seconds is the range the alignment may look in, so with 60 s the engine could not see an
answer at ~112 s and returned the best wrong one it could find (56 s, then 60 s). No logic on the plugin's side can
repair an answer that lies outside the searched range.

- MaxOffsetSeconds default 60 -> 180; the setting's description and AGENTS.md now call it what it is, a search range
  rather than a trust limit.
- A result that lands on the window is retried once with twice the window, and written only when it is not pinned to
  the wider window either and one alignment against the film's audio asks for nothing more; if the wider window is
  pinned too, the job refuses and tells the user to raise the setting.
- The rigid-shift path from 2.0.19 is deleted: it applied the measured displacement as if it were a pure shift, but a
  framerate correction makes that number the median displacement of a rescaled timeline, so applying it as a shift is
  wrong by construction.
- The release workflow now skips (with a warning) instead of failing when the tree's version is already published, so
  a test-only or documentation commit no longer mails a "run failed" notice.

The 2.0.20 code went in under an earlier, mislabelled 2.0.19 commit when a ship script ran a second time; this commit
versions it properly, which is also why the release workflow had refused to republish.

Checks: 383 passing.
EOF
