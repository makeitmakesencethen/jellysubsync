# A8 — the slow-profile worker sweep (transcript)

The plan's A8 is "bulk with `ParallelWorkers` = 1, 2, 4, 8 on the slow profile — the aggregate number that
matters". `a8_sweep.sh` ran exactly that on 2026-09-12 against the 2.38 GB episode
(`1c0799b1-407a-2e77-14b9-e6e0bffbd6e2`), one setting at a time, cold cache each time, and printed:

```
=== workers=1 2026-09-12T02:05:16+02:00 ===  exit=0 02:43:18
=== workers=2 2026-09-12T02:43:18+02:00 ===  exit=0 02:54:29
=== workers=4 2026-09-12T02:54:29+02:00 ===  exit=0 03:07:38
=== workers=8 2026-09-12T03:07:38+02:00 ===  exit=0 03:14:31

workers  tasks  completed  refused  failed  wall_s  per_task_s
      1     50         49        0       0  2277.2        None
      2     50         49        1       0   667.6        None
      4     50         38        0       4   786.1        None
      8     50         49        1       0   409.9        None
```

Notes that matter more than the numbers:

- **This transcript is the record of that sweep.** Its per-run JSON files (`acceptance-A8-slow-w*.json`) were
  overwritten minutes later by a second `a8_sweep.sh` invocation whose server did not have the slow-read shim
  active (that sweep took 16-37 s for the same 50 tasks). Those real measurements were kept and relabelled as
  `acceptance-A8-fast-w*.json`; the slow numbers live here.
- The **w=4 row is the odd one**: 4 of 50 tasks failed. The four failures are in the plugin log at
  01:05:35-01:07:35 UTC as `System.IO.DirectoryNotFoundException: Could not find a part of the path
  '/opt/data/jf12test/cache/subsync/<jobId>/subtitle_15.srt'` (streams 13, 14, 15, 19). That is finding S19
  in `knowledge/FIX_PLAN.md`: the shared extraction pass writes into a job's own temporary directory, and a job
  deletes that directory when it finishes, so jobs that start later read files their predecessor removed.
- w=1 at 2277 s is the honest cost of one worker on this profile: the extraction pass dominates and the jobs
  then serialize behind it.
