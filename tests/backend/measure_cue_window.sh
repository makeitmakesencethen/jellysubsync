#!/bin/bash
# Measures the cue-window change on the storage shape that matters: a share that charges ~13 ms per
# round trip (SLOWREAD_MS_PER_CALL), which is what fabji's Synology does. Before this change a cue-
# indexed file paid one read per cue; the log from his run shows 784 reads carrying 5.4 MB in a 21 s
# pass. The claim to verify is the read count and the pass time, so both are read back out of the log.
set -u
export LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu TZ=UTC
REPO=/opt/data/jellysubsync
JF=/opt/data/jf12test
LOG=/subsync-logs/subsync.log
TOKEN=$(cat /opt/data/tmp/jf_token.txt 2>/dev/null)
MEDIA="$JF/media-slow/Helikopterrånet S01E01.mkv"

# 1. the freshly built plugin, in the directory the test server loads
cp "$REPO/Jellyfin.Plugin.SubSync/bin/Release/net10.0/Jellyfin.Plugin.SubSync.dll" "$JF/data/plugins/SubSync_2.0.15.0/" || exit 1

# 2. restart with the per-round-trip shim, so this measures read latency rather than bytes
pkill -f "jellyfin.*--datadir" 2>/dev/null
sleep 6
( cd "$REPO/tests/backend" && SLOW=1 SLOWREAD_MS_PER_CALL=13 nohup ./start-server.sh >/tmp/measure-server.log 2>&1 & )
for i in $(seq 1 40); do
  code=$(curl -s -o /dev/null -w "%{http_code}" -m 5 http://127.0.0.1:8096/System/Info/Public)
  [ "$code" = "200" ] && break
  sleep 4
done
echo "server=$code"

# 3. one job on a slow file that has a cue index
ITEM=$(curl -s -m 20 -H "Authorization: MediaBrowser Token=$TOKEN" \
  "http://127.0.0.1:8096/Items?Recursive=true&IncludeItemTypes=Episode&SearchTerm=Helikopterr%C3%A5net&Limit=5" \
  | python3 -c "import sys,json;d=json.load(sys.stdin);print((d.get('Items') or [{}])[0].get('Id',''))")
echo "item=$ITEM"
[ -z "$ITEM" ] && { echo "no item"; exit 1; }

curl -s -m 30 -H "Authorization: MediaBrowser Token=$TOKEN" "http://127.0.0.1:8096/SubSync/Subtitles/$ITEM" \
  | python3 -c "import sys,json;d=json.load(sys.stdin);[print(s.get('Index'), s.get('Language'), s.get('IsExternal'), s.get('Codec')) for s in (d.get('Streams') or [])]" | head -5

JOB=$(curl -s -m 30 -X POST -H "Authorization: MediaBrowser Token=$TOKEN" -H 'Content-Type: application/json' \
  -d "{\"ItemId\":\"$ITEM\",\"SubtitleStreamIndex\":1}" "http://127.0.0.1:8096/SubSync/Sync" \
  | python3 -c "import sys,json;d=json.load(sys.stdin);print(d.get('Id') or d.get('id') or '')")
echo "job=$JOB"

for i in $(seq 1 60); do
  STATE=$(curl -s -m 20 -H "Authorization: MediaBrowser Token=$TOKEN" "http://127.0.0.1:8096/SubSync/Jobs/$JOB" \
    | python3 -c "import sys,json;d=json.load(sys.stdin);print(d.get('Status','') or d.get('State',''))" 2>/dev/null)
  case "$STATE" in *omplet*|*uccess*|*ail*|*efus*|*rror*) break;; esac
  sleep 5
done
echo "state=$STATE"

# 4. the numbers, straight out of the log
awk '/startup: version=2.0.20/{f=1} f' "$LOG" | grep -E "extract: storage|extract lane: .*->" | tail -4 | cut -c1-230
