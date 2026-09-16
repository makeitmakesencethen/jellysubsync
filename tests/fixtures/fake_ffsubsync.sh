#!/bin/sh
# Characterization harness: stands in for the bundled ffsubsync so RunSyncJob's terminals can be driven.
# Read by tests/job_checks.cs through the SUBSYNC_FAKE_ENGINE directory:
#
#   behaviour   payload | none | exit3 | exit1 | empty | chmod000      (what this run does)
#   behaviour.N                                                (overrides behaviour for call N only)
#   payload.N.srt                                              (what call N writes; payload.srt is the default)
#   stderr.txt / stderr.N.txt                                  (printed on stderr, all calls or call N only)
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

mode="$(cat "$dir/behaviour.$n" 2>/dev/null || cat "$dir/behaviour" 2>/dev/null || echo payload)"

# With this marker the stand-in behaves like the real engine on a reference it cannot open: a hard failure
# naming the path. Only the S46 case turns it on; the others model a lenient engine so they can exercise
# their own branch without the reference's existence being part of the question.
if [ -f "$dir/check-reference" ] && [ -n "$1" ] && [ ! -e "$1" ]; then
  echo "ffsubsync: unable to read reference $1; try ensuring file exists and has correct permissions" >&2
  exit 1
fi
out=""
prev=""
for a in "$@"; do
  if [ "$prev" = "--output" ] || [ "$prev" = "-o" ]; then out="$a"; fi
  prev="$a"
done

if [ -f "$dir/stderr.$n.txt" ]; then
  cat "$dir/stderr.$n.txt" >&2
else
  cat "$dir/stderr.txt" >&2 2>/dev/null || true
fi

case "$mode" in
  none) exit 0 ;;
  exit3) exit 3 ;;
  exit1) exit 1 ;;
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

# Asked to serialize the speech it just analysed, the real ffsubsync writes <reference>.npz next to the
# reference it was given, and the plugin harvests that into its speech cache. The P3 cache-hit case needs the
# same behaviour, or the file's second job can never find a stored analysis to reuse.
case " $* " in
  *" --serialize-speech "*)
    if [ -n "$1" ]; then
      printf 'npz' > "${1%.*}.npz"
    fi
    ;;
esac

exit 0
