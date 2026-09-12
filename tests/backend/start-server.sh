#!/bin/sh
# Start the test Jellyfin exactly the way the 2026-09-11 sessions started it.
#
# Added 2026-09-11 (session 2): the start line was only written down in prose in
# knowledge/backend-test-report-2026-09-11.md, and reconstructing it from the
# stdout logs cost time. Run this, do not retype it.
#
#   ./start-server.sh            # normal storage
#   SLOW=1 ./start-server.sh     # slow-storage profile (LD_PRELOAD shim, bytes are the cost)
#   SLOWREAD_MS_PER_CALL=13   # the same shim charging per round trip instead: a share charges ~13 ms
#                             #   per read whatever its size, which is the storage the prefetch has to
#                             #   be right for (measured on fabji's Synology)
#
# Logs:  /opt/data/jf12test/log/stdout-<SLOW|fast>.log
set -e

JF=/opt/data/jf12test
ICU=/opt/data/local/icu/usr/lib/x86_64-linux-gnu
HERE=$(cd "$(dirname "$0")" && pwd)
OUT="$JF/log/stdout-fast.log"

if [ -n "$SLOW" ]; then
  # The shim is built here rather than shipped: the 2026-09-11 session's copy lived only in a tmp
  # directory, so every run after it silently measured the fast profile while claiming slow (the
  # loader prints "cannot be preloaded" and carries on).
  if [ ! -f "$HERE/slowread.so" ]; then
    echo "building slowread.so" >&2
    gcc -shared -fPIC -O2 -o "$HERE/slowread.so" "$HERE/slowread.c" -ldl
  fi
  OUT="$JF/log/stdout-slow.log"
  export LD_PRELOAD="$HERE/slowread.so"
  export SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/
  export SLOWREAD_MS_PER_16K=12.8
fi

export LD_LIBRARY_PATH="$ICU"
export JELLYFIN_DATA_DIR="$JF/data"
export JELLYFIN_CONFIG_DIR="$JF/config"
export JELLYFIN_CACHE_DIR="$JF/cache"
export JELLYFIN_LOG_DIR="$JF/log"

cd "$JF/jellyfin"
exec /opt/data/.dotnet/dotnet jellyfin.dll >"$OUT" 2>&1
