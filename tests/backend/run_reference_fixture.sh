#!/bin/bash
# Deploy the current build, restart the test server without racing it, then run the framerate fixture.
#
# The two failed runs before this one failed for two different harness reasons, both fixed here: a readiness loop
# that carried on when the port was closed (so the fixture hit a dead server), and a restart that started the new
# instance before the old one had released the port (so the new one exited and the port ended up empty). The port
# is now waited on in both directions, and `set -e` is not used so a failure still prints its verdict.
set -u
REPO=/opt/data/jellysubsync
JF=/opt/data/jf12test
DLL="$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/Jellyfin.Plugin.SubSync.dll"

cp "$DLL" "$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/meta.json" "$JF/data/plugins/SubSync_2.0.15.0/"
echo "deployed $(md5sum "$JF/data/plugins/SubSync_2.0.15.0/Jellyfin.Plugin.SubSync.dll" | cut -d' ' -f1)"

pkill -f "[j]ellyfin.dll" || true
for i in $(seq 1 60); do
  curl -s -m 4 -o /dev/null http://127.0.0.1:8096/System/Info/Public || break
  sleep 2
done
echo "port released after $i checks"

cd "$REPO/tests/backend"
setsid nohup ./start-server.sh >/dev/null 2>&1 &
for i in $(seq 1 90); do
  C=$(curl -s -m 6 -o /dev/null -w "%{http_code}" http://127.0.0.1:8096/System/Info/Public)
  [ "$C" = "200" ] && break
  sleep 4
done
echo "server=$C after $i checks"
if [ "$C" != "200" ]; then
  echo "ABORT: no server, fixture not run"
  exit 1
fi

# Confirm the running server is the build just deployed, not a survivor of the previous one.
echo "startup: $(grep -o 'version=[0-9.]*' "$JF/data/data/subsync/logs/subsync.log" | tail -1)"

OLD=$(stat -c %Y reference_framerate.json 2>/dev/null || echo 0)
timeout 1500 python3 reference_framerate.py > /opt/data/tmp/reference_framerate.log 2>&1
echo "pal fixture exit=$?"
for i in $(seq 1 40); do
  N=$(stat -c %Y reference_framerate.json 2>/dev/null || echo 0)
  [ "$N" != "$OLD" ] && break
  sleep 10
done
python3 - <<'PY'
import json
d = json.load(open('/opt/data/jellysubsync/tests/backend/reference_framerate.json'))
keys = ('status', 'rescale', 'span_ratio_vs_reference', 'median_offset_s', 'refused', 'written')
print('verdict:', d.get('verdict'))
print('ON :', {k: d['framerate-on'].get(k) for k in keys})
print('OFF:', {k: d['framerate-off'].get(k) for k in keys})
PY
tail -4 /opt/data/tmp/reference_framerate.log
echo "---"
tail -8 /opt/data/tmp/reference_mistimed.log
