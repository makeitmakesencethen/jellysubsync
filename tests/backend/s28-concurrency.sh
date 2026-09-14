#!/bin/bash
# S28 concurrency experiment: per-file wall clock for N concurrent audio-ruler walks on one volume.
#
# The shim charges per round trip and per 16 KB, per process, so on its own it models N readers each
# getting the full share - which cannot show contention. A shared volume is therefore modelled by
# dividing it between the readers, two ways:
#   bandwidth        : the bytes are shared  -> per-process bytes cost x N (calls unchanged)
#   bandwidth+queue  : bytes AND round trips are shared -> per-call latency x N as well
#                      (a share that serialises round trips makes each read wait longer, which is the
#                       shape the live run showed: 10 ms measured, 1 419-1 613 ms observed under load)
# The real share sits somewhere between the two; the shim cannot tell us where, so both are run.
#
# Each "walk" is what an audio-ruler job costs the engine: ffsubsync decoding the media's audio
# (`--vad webrtc --reference-stream a:0` forces the audio path, no subtitle search).
#
# Fixture: a 218 MB / 5 minute slice of a real episode inside the shim's prefix, with a 59-cue sidecar.
#   one walk ~50 000-80 000 reads, so per-file time is dominated by the per-call term, as in the field.
FFS=/opt/data/ffsbuild/bin/ffsubsync
SLICE="/opt/data/jf12test/media-slow/s28-slice.mkv"
INPUT="/opt/data/jf12test/media-slow/s28-slice.srt"
PREFIX=/opt/data/jf12test/media-slow/
SHIM=/opt/data/jellysubsync/tests/backend/slowread.so
OUT=/tmp/s28-conc
report=/tmp/s28-concurrency.txt
mkdir -p "$OUT"; rm -f "$OUT"/*
: > "$report"

echo "fixture: $(stat -c%s "$SLICE") bytes; models: bandwidth (bytes x N), bandwidth+queue (bytes x N, calls x N)" >> "$report"
echo >> "$report"

for model in bandwidth bandwidth+queue; do
  for n in 1 2 4 8; do
    ms16=$(python3 -c "print(round(1.46*$n, 3))")
    if [ "$model" = "bandwidth+queue" ]; then msc=$(python3 -c "print(round(10*$n, 3))"); else msc=10; fi
    echo "=== model=$model n=$n (per process: ${ms16} ms/16KB, ${msc} ms/call)" >> "$report"
    level_start=$(date +%s)
    for i in $(seq 1 "$n"); do
      (
        start=$(date +%s%N)
        env LD_PRELOAD=$SHIM SLOWREAD_PREFIX=$PREFIX SLOWREAD_MS_PER_16K=$ms16 SLOWREAD_MS_PER_CALL=$msc \
          timeout 3600 $FFS "$SLICE" -i "$INPUT" -o "$OUT/out-${model}-n${n}-$i.srt" --vad webrtc --reference-stream a:0 \
          > "$OUT/log-${model}-n${n}-$i.txt" 2>&1
        rc=$?
        end=$(date +%s%N)
        echo "$(( (end - start) / 1000000 )) $rc" > "$OUT/job-${model}-n${n}-$i.time"
      ) &
    done
    wait
    level_wall=$(( $(date +%s) - level_start ))
    echo "LEVEL model=$model n=$n wall=${level_wall}s (slowest of $n)" >> "$report"
    for i in $(seq 1 "$n"); do
      read -r ms rc < "$OUT/job-${model}-n${n}-$i.time" 2>/dev/null
      wrote=$(test -s "$OUT/out-${model}-n${n}-$i.srt" && echo yes || echo no)
      echo "  job $i: ${ms} ms, exit=$rc, wrote_output=$wrote" >> "$report"
    done
    echo >> "$report"
  done
done

echo "=== summary (per-file ms; aggregate = how many files per hour the volume delivers)" >> "$report"
python3 - "$OUT" >> "$report" <<'PY'
import sys, glob, os, statistics, re
out = sys.argv[1]
for model in ("bandwidth", "bandwidth+queue"):
    for n in (1, 2, 4, 8):
        times = []
        for f in sorted(glob.glob(f"{out}/job-{model}-n{n}-*.time")):
            try:
                ms, rc = open(f).read().split()
                times.append(int(ms) / 1000)
            except Exception:
                pass
        if not times:
            continue
        med = statistics.median(times)
        agg = n * 3600 / max(1.0, max(times))
        print(f"  {model:16} n={n}: per-file median {med:6.1f}s  min {min(times):6.1f}s  max {max(times):6.1f}s"
              f"   aggregate {agg:5.2f} files/hour")
PY
echo "=== done" >> "$report"
