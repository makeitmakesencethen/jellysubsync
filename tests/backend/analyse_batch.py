#!/usr/bin/env python3
"""What one small batch actually did, from the plugin's own log.

    python3 analyse_batch.py <label-substring> [lines-after]
"""
import re
import sys

LOG = "/subsync-logs/subsync.log"
needle = sys.argv[1] if len(sys.argv) > 1 else "Mammut"
window = int(sys.argv[2]) if len(sys.argv) > 2 else 400

lines = open(LOG, encoding="utf-8", errors="replace").read().splitlines()
start = max(i for i, l in enumerate(lines) if needle.lower() in l.lower())
chunk = lines[start:start + window]

counts = {
    "storage probes": 0, "lane passes": 0, "extract cache": 0, "extract seekhead": 0,
    "extract shared-pass": 0, "extract reused": 0, "ffsubsync starts": 0, "jobs queued": 0,
    "reference from subtitle": 0, "reference from audio": 0,
}
lane_bytes = lane_reads = lane_ms = 0
for l in chunk:
    if "extract: storage" in l:
        counts["storage probes"] += 1
    if "extract lane:" in l and "->" in l:
        counts["lane passes"] += 1
        m = re.search(r"([\d,\.]+) MB, (\d+) reads, ([\d ]+) ms", l)
        if m:
            lane_bytes += float(m.group(1).replace(",", "."))
            lane_reads += int(m.group(2))
            lane_ms += float(m.group(3).replace(" ", ""))
    if "extract: method=cache" in l:
        counts["extract cache"] += 1
    if "extract: method=seekhead-cues" in l:
        counts["extract seekhead"] += 1
    if "extract: method=shared-pass" in l:
        counts["extract shared-pass"] += 1
    if "extract: method=reused" in l:
        counts["extract reused"] += 1
    if "ffsubsync start" in l:
        counts["ffsubsync starts"] += 1
    if "queued: job=" in l:
        counts["jobs queued"] += 1
    if "reference: method=subtitle" in l:
        counts["reference from subtitle"] += 1
    if "reference: method=audio" in l or "reference=(default)" in l:
        counts["reference from audio"] += 1

print("batch line:", chunk[0][:150])
print("next %d lines: %s" % (window, counts))
print("lane passes read %.0f MB in %d reads over %.0f ms (%.1f ms per read)" % (
    lane_bytes, lane_reads, lane_ms, (lane_ms / lane_reads) if lane_reads else 0))
print("\n--- markers ---")
for l in chunk:
    if any(k in l for k in ("extract: storage", "extract lane:", "extract: method=", "ffsubsync start",
                            "jobs queued", "reference: method=", "batch ", "switching to", "reads measured")):
        print(" ", l.split("INFO", 1)[-1].split("WARN", 1)[-1].strip()[:170])
