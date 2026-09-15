#!/bin/sh
# Characterization harness: stands in for the bundled ffsubsync so RunSyncJob's terminals can be driven.
# Read by tests/job_checks.cs through the SUBSYNC_FAKE_ENGINE directory:
#
#   behaviour   payload | none | exit1 | empty | chmod000      (what this run does)
#   payload.N.srt                                              (what call N writes; payload.srt is the default)
#   stderr.txt                                                 (printed on stderr)
#   argv.log                                                   (every invocation is appended here)
#
# A payload whose first bytes are __NONE__ makes the call write nothing; __DELETE__ removes whatever is at the
# output path, which is how "the second run produced nothing" is expressed when the first run already wrote
# something to the same path.
dir="$(dirname "$0")"

case "$*" in
  *--version*)
    echo "ffsubsync 0.5.1"
    exit 0
    ;;
esac

n=$(( $(cat "$dir/calls" 2>/dev/null || echo 0) + 1 ))
echo "$n" > "$dir/calls"
printf '%s\n' "$*" >> "$dir/argv.log"

mode="$(cat "$dir/behaviour" 2>/dev/null || echo payload)"
out=""
prev=""
for a in "$@"; do
  if [ "$prev" = "--output" ] || [ "$prev" = "-o" ]; then out="$a"; fi
  prev="$a"
done

cat "$dir/stderr.txt" >&2 2>/dev/null || true

case "$mode" in
  none) exit 0 ;;
  exit1) exit 3 ;;
  empty)
    [ -n "$out" ] && : > "$out"
    exit 0 ;;
  chmod000)
    payload="$dir/payload.$n.srt"
    [ -f "$payload" ] || payload="$dir/payload.srt"
    if [ -f "$payload" ] && [ -n "$out" ]; then
      cp "$payload" "$out"
      chmod 000 "$out"
    fi
    exit 0 ;;
esac

payload="$dir/payload.$n.srt"
[ -f "$payload" ] || payload="$dir/payload.srt"
if [ -f "$payload" ] && [ -n "$out" ]; then
  head="$(head -c 12 "$payload")"
  case "$head" in
    __NONE__*) ;;
    __DELETE__*)
      rm -f "$out" ;;
    *)
      cp "$payload" "$out" ;;
  esac
fi

exit 0
