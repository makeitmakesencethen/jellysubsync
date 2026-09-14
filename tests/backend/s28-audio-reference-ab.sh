#!/bin/bash
# S28 A/B: does handing ffsubsync the whole media file (the plugin's symlink case) cost what the
# compact audio-only reference costs, on a volume with fabji's measured profile?
#   slow profile: 10 ms per round trip + 11 MB/s (1.46 ms per 16 KB) — slowread-fabji.env
# The slow runs get the REAL path inside the shim's prefix, which is what the plugin's symlink resolves
# to (the shim resolves descriptors through /proc/self/fd, so a symlink on the state volume escapes it).
# Environment is passed with `env` inside the timeout so LD_PRELOAD actually reaches the child.
FFS=/opt/data/ffsbuild/bin/ffsubsync
MEDIA="/opt/data/jf12test/media-slow/Helikopterrånet S01E01.mkv"
INPUT="/opt/data/jf12test/media-slow/Helikopterrånet S01E01.SYNCED.eng.srt"
FLAC=/tmp/heli-audio.flac
SHIM=/opt/data/jellysubsync/tests/backend/slowread.so
report=/tmp/s28-ab.txt
: > "$report"

echo "media: $(stat -c%s "$MEDIA") bytes; slow profile: 10 ms/call + 1.46 ms per 16 KB" >> "$report"
echo >> "$report"

run() {
  name=$1; cap=$2; shift 2
  echo "=== $name (cap ${cap}s)" >> "$report"
  SECONDS=0
  timeout "$cap" "$@" >> "$report" 2>&1
  rc=$?
  echo "RESULT $name: ${SECONDS}s, exit=$rc" >> "$report"
  echo >> "$report"
}

# 1) the fix's one-time extraction of the compact reference, through the slow profile (once per file)
run "C_slow_extract_flac" 900 env LD_PRELOAD=$SHIM SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ \
    SLOWREAD_MS_PER_CALL=10 SLOWREAD_MS_PER_16K=1.46 \
    ffmpeg -y -v error -i "$MEDIA" -map 0:a:0 -vn -ac 1 -ar 16000 -c:a flac "$FLAC"
echo "flac: $(stat -c%s "$FLAC" 2>/dev/null) bytes" >> "$report"
echo >> "$report"

# 2) decode-only cost, isolating I/O from VAD: through the share vs local
run "D_slow_decode_audio_from_media" 900 env LD_PRELOAD=$SHIM SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ \
    SLOWREAD_MS_PER_CALL=10 SLOWREAD_MS_PER_16K=1.46 \
    ffmpeg -v error -i "$MEDIA" -map 0:a:0 -vn -f null -
run "E_local_decode_audio_from_flac" 180 ffmpeg -v error -i "$FLAC" -f null -

# 3) the engine: media as reference through the slow profile (the plugin's case) vs the local audio file
run "A_slow_engine_media_reference" 900 env LD_PRELOAD=$SHIM SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ \
    SLOWREAD_MS_PER_CALL=10 SLOWREAD_MS_PER_16K=1.46 \
    $FFS "$MEDIA" -i "$INPUT" -o /tmp/a-slow.srt \
    --max-offset-seconds 150 --max-subtitle-seconds 10 --vad subs_then_webrtc --reference-stream a:0 --serialize-speech
run "B_local_engine_flac_reference" 600 $FFS "$FLAC" -i "$INPUT" -o /tmp/b-local.srt --vad subs_then_webrtc
run "B_slow_engine_flac_reference" 600 env LD_PRELOAD=$SHIM SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ \
    SLOWREAD_MS_PER_CALL=10 SLOWREAD_MS_PER_16K=1.46 \
    $FFS "$FLAC" -i "$INPUT" -o /tmp/b-slow.srt --vad subs_then_webrtc

echo "=== the alignment each run produced (they must agree for the fix to be behaviour-preserving)" >> "$report"
grep -h "offset seconds\|framerate scale factor" "$report" | sed 's/^ *//' >> "$report"
echo "=== done" >> "$report"
