#!/bin/sh
# Characterization harness: stands in for ffmpeg where RunSyncJob's embedded path needs a container probe.
#
# Run by the plugin as `ffmpeg -i <file>` (see ResolveContainerSubtitleIndexAsync) and as the extraction
# fallback. For the probe it prints FakeFfmpeg's banner on stderr and exits 1, which is what the real ffmpeg
# does for an input it cannot decode; for anything else it writes the prepared subtitle to the last argument.
dir="$(dirname "$0")"

if [ "$#" -le 2 ]; then
  cat "$dir/banner.txt" >&2
  exit 1
fi

last=""
for a in "$@"; do last="$a"; done
cp "$dir/extracted.srt" "$last"
exit 0
