# GOAL: make the walk ceiling judge volumes by ratio, not by constants from one machine (S39)

## Why this exists

Every number the ceiling decides with was measured on one machine, and the code says so in its own comments:

    SubSyncService.cs:2769  SlowReadMsPerCall = 5.0
      /// … far below fabji's share, which measures 13-46 ms per call at rest
    SubSyncService.cs:2775  ThrashingReadMsPerCall = 100.0
      /// … the bottom of what that share shows when it is thrashing (1419-1613 ms per call while eight walks ran)
    SubSyncService.cs:2833  FastWalkMbPerSec = 50.0
      /// … 2,2x above every walk this share has produced and 1,7x below every local walk
    SubSyncService.cs:2839  ThrashingWalkMbPerSec = 1.0

The failure that matters: a share or NAS that genuinely delivers more than 50 MB/s - 10 GbE, or a NAS backed by
NVMe - is classified "fast" and gets **no ceiling at all**, so eight full-file walks land on one volume. That is
exactly the thrash this ceiling exists to prevent, and it happens on setups *better* than the one the constants
came from, silently.

The second failure is visible in the field already, from 2026-09-14: a volume's classification flipping between
planning passes within a single batch. The local NVMe walked at 68-91 MB/s alone and 45-58 MB/s alongside a slow
share's walks, straddling the 50 MB/s boundary, and after `walk ceiling: holding /Media|/dev/nvme0n1p2 at 2
concurrent media read(s) - this volume's last walk moved 45,6 MB/s, which is storage-bound` the batch sat at
`dispatch: starting 1, running 1` for twenty-five minutes. S37 removed the contaminated sample that caused this
one; the boundaries themselves are still absolute numbers that a volume can straddle.

## GOAL

Classify each volume relative to the best this machine has measured, with hysteresis so a volume's ceiling cannot
oscillate, and with the outcome on the machine the constants came from unchanged - the ratios must reproduce
today's field results there, because a generalisation that changes behaviour on the machine it was written from
is a second bug, not a generalisation.

## Work

1. **The reference: the machine's own best.** In `VolumeProfiles`, add `FastestReadMsPerCall()` and
   `FastestWalkBytesPerMs()`, computed over the profiles that have samples, requiring a minimum sample count
   (8 is a reasonable start) so a single read cannot set the bar. The read reference is floored at ~0,05 ms per
   call: no storage answers in less than that, and anything faster is the page cache rather than the disk -
   letting a cache-fast volume be the reference would make every real disk look slow. Walk throughput needs no
   floor; the device bounds it. Derive the reference at decision time rather than caching it, so it reflects
   what the process has actually measured when it decides.
2. **Ratios replace the constants in the ceiling decision.** Storage-bound means either reads above ~10-20x the
   read reference, or uncontended walks below ~1/5 of the walk reference. Pick one number in each band, justify
   it in the comment with the field ranges, and state the reasoning that makes it right for *fast* volumes too: a
   150 MB/s NAS against a 500 MB/s local disk is still ~1/3 of the reference, and eight walks on it would
   saturate its link at ~19 MB/s each. Capping it at 2 leaves the aggregate where it was and gives each file
   ~75 MB/s instead - the share's own shape.
3. **Hysteresis, because flipping is what broke the field runs.** Cap above the upper ratio, uncap only below a
   lower one (roughly half of it), so no volume's ceiling can oscillate between planning passes while a batch
   runs. Write down that this is why the band exists - the reason is what a future reader will otherwise remove
   as redundant.
4. **The walks decide; the reads only tighten.** The walks are the operation being limited, so when a volume has
   uncontended walk measurements they carry the decision. The read signal may hold a volume whose reads are slow,
   but it must not over-cap a volume whose own uncontended walks say it is fine - that is precisely the mistake
   that held a local NVMe at 2 concurrent walks. A volume with no measurement of any kind stays at the
   conservative 2, as now.
5. **Every reason must name the reference it was compared against**, in the shape S32 established:
   `this volume's last walk moved 3,4 MB/s against a best of 91,2 MB/s measured on this machine, which is
   storage-bound`. A ratio rule whose log line does not say which two numbers produced it is unauditable in the
   field, which is where it will be judged.
6. **Demote the constants to documentation.** They stay in the file as the ranges the field has shown, and they
   stop deciding anything. Where a fallback is genuinely needed - a process that has measured nothing at all -
   say so explicitly and keep it conservative rather than inventing a number.
7. **The checks have to force generality.** Add a second synthetic machine to the suite: a *fast* NAS, ~1 ms per
   read and ~150 MB/s, alongside the existing slow-NAS shim. Both must be classified correctly against the local
   volume, and the existing machine's field numbers must be reproduced as checks (its share's 13-46 ms per read
   against a sub-millisecond local volume is storage-bound; that local volume's 85-91 MB/s walks against the same
   reference are not).

## Non-negotiables

- **No new setting.** The classification is derived from measurements, never configured.
- **Fail closed.** An unmeasured volume is held at 2, not treated as fast. That rule does not change.
- **A latency proxy must never over-cap a volume whose own walks are fine.** Combining the two signals by taking
  the more conservative one is what the current code does, and it is the specific mechanism of the complaint that
  started this.
- **Any number that still decides must be either a ratio or a documented floor** (a physical limit, like the page
  cache's), never a threshold lifted from one machine's measurements.
- **Reproduce the current behaviour on the machine the constants came from.** Treat the field numbers on
  2026-09-14 as the regression test for the generalisation.
- One item, one commit; suite green (**603 checks or more**) after every step; the FIX_PLAN row for S39 updated
  with the ratios chosen, why, the commit, and the field run owed; hold before pushing or bumping a version.

## Verification - what "done" means

- **Two-machine checks**: the slow-NAS profile and the fast-NAS profile are each classified correctly against the
  local volume; unmeasured is still held at 2; a volume equal to the reference is uncapped; a volume ~20x the
  reference is capped; and the hysteresis band behaves (a volume just outside the cap ratio holds its ceiling, a
  volume just inside the release ratio lets go).
- **The field numbers as checks**: the existing machine's readings - 13-46 ms per read on the share, 85-91 MB/s
  walks on local NVMe - still produce storage-bound for one and no ceiling for the other.
- **No oscillation by construction**: a check drives a volume through a sequence of measurements that straddle
  the old boundary (68-91, then 45-58) and asserts the ceiling never moves once set, which is the failure the
  field run showed.
- **A mixed-batch field run on the server**: the local volume reading `ceiling none` for the whole run while the
  share reads `storage-bound`, both stated against the reference in the log, with no flipping. That run - the
  same shape as the one that showed `starting 1, running 1` for twenty-five minutes - is what closes S39.
- The pricing is *not* touched here: it shares these constants, and it is the next item.

## Failure modes this goal must not repeat

- **Inventing another absolute constant.** If a number decides something, it is a ratio to the machine's own best
  or a documented physical floor. Anything else will be wrong on the next machine, silently.
- **Letting a page-cache read set the reference**, which would make every disk in the world look slow.
- **One-sided hysteresis** (cap low, release low), which still flips - just less often.
- **Over-correcting**: generalising so eagerly that the machine the constants came from starts mis-classifying,
  which is the exact complaint that produced this goal.
- **Calling it done from the suite.** The suite has one machine's shim in it; the mixed-batch field run is what
  proves the generalisation, and it is the run that has already failed twice with absolute thresholds.
