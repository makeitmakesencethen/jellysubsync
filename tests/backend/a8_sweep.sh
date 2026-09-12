#!/usr/bin/env bash
# A8 — the worker sweep on the slow profile.
#
# The plan's A8 is "bulk with ParallelWorkers = 1, 2, 4, 8 on the slow profile — the aggregate number
# that matters". One run per setting, same file, same cold cache, sequential so the numbers come from
# the same machine state. Each run writes acceptance-A8-slow-w<W>.json next to this script.
#
#   tests/backend/a8_sweep.sh [item-id]
set -u

cd "$(dirname "$0")"
ITEM="${1:-1c0799b1407a2e7714b9e6e0bffbd6e2}"   # the 2.38 GB episode on the slow media folder
LOG=/opt/data/tmp/a8_sweep.log

: > "$LOG"
for W in 1 2 4 8; do
    echo "=== workers=$W $(date -Is) ===" | tee -a "$LOG"
    python3 acceptance.py --scope episode --item "$ITEM" --label "A8-slow-w${W}" \
        --workers "$W" --timeout 3600 >> "$LOG" 2>&1
    echo "exit=$? $(date -Is)" | tee -a "$LOG"
done

echo "=== sweep done $(date -Is) ===" | tee -a "$LOG"
python3 - <<'PY' | tee -a "$LOG"
import json, os
rows = []
for w in (1, 2, 4, 8):
    p = os.path.join(os.path.dirname(os.path.abspath(__file__)), "acceptance-A8-slow-w%d.json" % w)
    if not os.path.exists(p):
        rows.append((w, None))
        continue
    d = json.load(open(p))
    rows.append((w, d))
print("workers  tasks  completed  refused  failed  wall_s  per_task_s")
for w, d in rows:
    if not d:
        print("%7d  (no result file)" % w)
        continue
    print("%7d  %5s  %9s  %7s  %6s  %6s  %10s" % (
        w, d.get("tasks"), d.get("completed"), d.get("refused"), d.get("failed"),
        d.get("wall_s"), d.get("wall_per_task")))
PY
