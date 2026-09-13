#!/bin/bash
# 2.0.28: the queue hands the extraction lane a subtitle ordinal, not Jellyfin's all-streams index.
set -u
set -o pipefail
export LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu TZ=UTC
REPO=/opt/data/jellysubsync
cd "$REPO" || exit 1

python3 - <<'PY'
import pathlib
for path, old, new in (
    ('Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj', '<Version>2.0.27.0</Version>', '<Version>2.0.28.0</Version>'),
    ('Jellyfin.Plugin.SubSync/meta.json', '"version": "2.0.27.0"', '"version": "2.0.28.0"'),
    ('build.yaml', 'version: "2.0.27.0"', 'version: "2.0.28.0"'),
):
    p = pathlib.Path(path); t = p.read_text()
    if new in t:
        continue
    assert old in t, path
    p.write_text(t.replace(old, new, 1))

c = pathlib.Path('CHANGELOG.md'); s = c.read_text()
if '## 2.0.28 (beta)' not in s:
    old = '## 2.0.27 (beta)'
    new = """## 2.0.28 (beta)

The queue now names a queued subtitle the same way the extraction lane counts subtitles.

A job is queued against a subtitle stream, and Jellyfin numbers that stream by its position among *every* stream
in the file - video and audio included. The extraction lane, the subtitle cache and the one-pass reader count
subtitle tracks only, so on any file whose subtitles are not its first streams the two numbers differ by however
many streams sit in front of them. The queue stored Jellyfin's number and handed it to code that wanted the other
one.

- **Five jobs of the last test run were refused for it**, all in the same shape, all on files whose subtitles sit
  behind a video and one or more audio streams: `subtitle ordinal 11 out of range (11 tracks)` (Egghead Republic,
  and the lav track of four Thunder in My Heart episodes). On files where the wrong number happened to land inside
  the range, the extraction lane read a neighbouring track and cached its text under a key no job ever reads - a
  wasted pass, not a wrong subtitle, because every job resolves its own stream with ffmpeg and extracts what it
  needs itself.
- **The translation happens once, at the queue.** Reproduced first on a fixture of the failed file's shape (one
  video stream, one audio stream, eleven subtitle tracks: stream 11 is the tenth subtitle, ordinal 9). Before, the
  queue handed over 11 and the lane refused it with the line above; after, it hands over 9 and the lane extracts
  that track's own text. One definition now serves the queue and the run-time resolver, so the track a job was
  queued for and the track it reads cannot drift apart.
- **The queued log line names both numbers** (`stream=` and `ordinal=`), so reading the log cannot confuse the two
  again.

Nothing changes for files whose subtitles are their first streams: the two numbers are the same there, which is why
24 of the 29 extraction passes in that run were never affected.

## 2.0.27 (beta)"""
    assert old in s
    c.write_text(s.replace(old, new, 1))
print('2.0.28 versioned and recorded')
PY

/opt/data/.dotnet/dotnet build Jellyfin.Plugin.SubSync/Jellyfin.Plugin.SubSync.csproj -c Release 2>&1 | grep -E "error|Build succeeded" | head -3

python3 tests/run_checks.py > /tmp/release_2_0_28_checks.txt 2>&1
SUITE=$?
echo "suite exit=$SUITE, $(grep -c '^PASS' /tmp/release_2_0_28_checks.txt) checks, $(grep -c '^FAIL' /tmp/release_2_0_28_checks.txt) failures"
if [ "$SUITE" != "0" ]; then
  echo "NOT PUSHING: checks failing"
  grep '^FAIL' /tmp/release_2_0_28_checks.txt | head -5
  exit 1
fi

git add -A && git commit -q -F - <<'EOF' && git push origin beta 2>&1 | tail -1 && echo PUSHED
2.0.28: the queue hands the extraction lane a subtitle ordinal, not a stream index

S14, the contract half. The enqueue path stored the selected stream's own MediaStream.Index as the subtitle's
ordinal - the number the extraction lane, the subtitle cache and SiblingOrdinals count subtitle tracks by.
Jellyfin's Index counts every stream in the file, video and audio included, so the two agree only when a file's
subtitles are its first streams. On the last test run they did not: five jobs were refused with "subtitle ordinal
11 out of range (11 tracks)" (Egghead Republic and the lav track of four Thunder in My Heart episodes), and where
the leaked number landed inside the range the lane read a neighbouring track for its cache instead.

Reproduced first on a fixture of that shape (make_remux.py --sub-tracks 11: one video, one audio, eleven
subtitle tracks, so stream 11 is the tenth subtitle, ordinal 9). One definition,
SubSyncService.EmbeddedSubtitleOrdinal, now serves the enqueue path and the run-time resolver, and the queued log
line names both numbers (stream= and ordinal=).

S14's other half stays open: a job still addresses its track by the Index captured at queue time, so numbering
that shifts between queueing and running resolves to whatever holds that number by then.

505 checks green.
EOF
