#!/bin/bash
# S28 part 2 (C was capped at 900 s and is recorded as ">=15 min"): the engine comparison and the
# decode-only cost. Same slow profile as part 1: 10 ms/round trip + 1.46 ms per 16 KB.
FFS=/opt/data/ffsbuild/bin/ffsubsync
MEDIA="/opt/data/jf12test/media-slow/Helikopterrånet S01E01.mkv"
INPUT="/opt/data/jf12test/media-slow/Helikopterrånet S01E01.SYNCED.eng.srt"
FLAC=/tmp/heli-audio.flac
SHIM=/opt/data/jellysubsync/tests/backend/slowread.so
report=/tmp/s28-ab2.txt
: > "$report"
run() { name=$1; cap=$2; shift 2; echo "=== $name (cap ${cap}s)" >> "$report"; SECONDS=0; timeout "$cap" "$@" >> "$report" 2>&1; rc=$?; echo "RESULT $name: ${SECONDS}s, exit=$rc" >> "$report"; echo >> "$report"; }
slowenv() { env LD_PRELOAD=$SHIM SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ SLOWREAD_MS_PER_CALL=10 SLOWREAD_MS_PER_16K=1.46 "$@"; }
run "D_slow_decode_audio_from_media" 900 slowenv ffmpeg -v error -i "$MEDIA" -map 0:a:0 -vn -f null -
run "E_local_decode_audio_from_flac" 180 ffmpeg -v error -i "$FLAC" -f null -
run "A_slow_engine_media_reference" 1200 slowenv $FFS "$MEDIA" -i "$INPUT" -o /tmp/a-slow.srt --max-offset-seconds 150 --max-subtitle-seconds 10 --vad subs_then_webrtc --reference-stream a:0 --serialize-speech
run "B_local_engine_flac_reference" 600 $FFS "$FLAC" -i "$INPUT" -o /tmp/b-local.srt --vad subs_then_webrtc
run "B_slow_engine_flac_reference" 600 slowenv $FFS "$FLAC" -i "$INPUT" -o /tmp/b-slow.srt --vad subs_then_webrtc
echo "=== alignment each run produced" >> "$report"
grep -h "offset seconds\|framerate scale factor" "$report" | sed 's/^ *//' >> "$report"
echo "=== done" >> "$report"
