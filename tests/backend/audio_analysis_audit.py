#!/usr/bin/env python3
"""How often one file's audio was analysed more than once, from the plugin's own log."""
import collections
import re

LOG = "/subsync-logs/subsync.log"
lines = open(LOG, encoding="utf-8", errors="replace").read().splitlines()

analyses = collections.Counter()
for l in lines:
    if "reference: method=audio" in l or "reference: method=speech-cache" in l:
        m = re.search(r"args=(\S+)|file=(\S+)", l)
    if "reference: method=audio" in l:
        m = re.search(r"\[([0-9a-f]{32})\]", l)
        analyses[m.group(1) if m else "?"] += 1

# group by the mp4/mkv the job was for: find the job's file from its ffsubsync start line
files = collections.defaultdict(list)
jobfile = {}
for l in lines:
    m = re.search(r"\[([0-9a-f]{32})\].*ffsubsync start:.*?args=(\S+)", l)
    if m:
        jobfile[m.group(1)] = m.group(2)
for job, path in jobfile.items():
    name = path.split("/")[-1] if path else "?"
    files[name].append(job)

print("audio analyses per job: %d jobs did their own analysis" % sum(analyses.values()))
print("jobs on the speech cache: %d" % len([l for l in lines if "method=speech-cache" in l]))
print()
multi = [(f, len(j)) for f, j in files.items() if len(j) > 1]
print("files that ran ffsubsync more than once: %d" % len(multi))
for f, n in sorted(multi, key=lambda x: -x[1])[:8]:
    print("  %-64s %d runs" % (f[:64], n))
