#!/bin/bash
# S28 part 3: the shimmed full-file passes, with the environment applied inline (timeout cannot exec a
# shell function - that is what killed parts 1 and 2's shimmed runs with exit 127).
FFS=/opt/data/ffsbuild/bin/ffsubsync
MEDIA="/opt/data/jf12test/media-slow/Helikopterrånet S01E01.mkv"
INPUT="/opt/data/jf12test/media-slow/Helikopterrånet S01E01.SYNCED.eng.srt"
FLAC=/tmp/heli-audio.flac
SHIM=/opt/data/jellysubsync/tests/backend/slowread.so
report=/tmp/s28-ab3.txt
: > "$report"
run() { name=$1; cap=$2; shift 2; echo "=== $name (cap ${cap}s)" >> "$report"; SECONDS=0; timeout "$cap" "$@" >> "$report" 2>&1; rc=$?; echo "RESULT $name: ${SECONDS}s, exit=$rc" >> "$report"; echo >> "$report"; }
run "D_slow_decode_audio_from_media" 1200 env LD_PRELOAD=$SHIM SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ SLOWREAD_MS_PER_CALL=10 SLOWREAD_MS_PER_16K=1.46 ffmpeg -v error -i "$MEDIA" -map 0:a:0 -vn -f null -
run "A_slow_engine_media_reference" 1800 env LD_PRELOAD=$SHIM SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ SLOWREAD_MS_PER_CALL=10 SLOWREAD_MS_PER_16K=1.46 $FFS "$MEDIA" -i "$INPUT" -o /tmp/a-slow.srt --max-offset-seconds 150 --max-subtitle-seconds 10 --vad subs_then_webrtc --reference-stream a:0 --serialize-speech
run "B_slow_engine_flac_reference" 600 env LD_PRELOAD=$SHIM SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ SLOWREAD_MS_PER_CALL=10 SLOWREAD_MS_PER_16K=1.46 $FFS "$FLAC" -i "$INPUT" -o /tmp/b-slow.srt --vad subs_then_webrtc
echo "=== done" >> "$report"
