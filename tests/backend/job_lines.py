#!/usr/bin/env python3
"""Every line the plugin wrote about the jobs of one batch, in order."""
import sys

LOG = "/subsync-logs/subsync.log"
needle = sys.argv[1]
extra = sys.argv[2] if len(sys.argv) > 2 else None

lines = open(LOG, encoding="utf-8", errors="replace").read().splitlines()
start = max(i for i, l in enumerate(lines) if needle.lower() in l.lower())
chunk = lines[start:start + 600]
ids = set()
for l in chunk[:120]:
    for m in l.split():
        if len(m) == 34 and m[0] == "[" and m[-1] == "]":
            ids.add(m.strip("[]"))

print("job ids in that batch:", sorted(ids))
print()
for l in lines:
    if any(i in l for i in ids) or (extra and extra.lower() in l.lower()):
        print(l.split("INFO", 1)[-1].split("WARN", 1)[-1].split("ERROR", 1)[-1].strip()[:185])
