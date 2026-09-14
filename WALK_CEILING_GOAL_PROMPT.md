# GOAL: make the walk ceiling's measurement trustworthy (S32 + S33)

## Why this exists

2.0.31 ships a per-volume ceiling on audio-ruler walks, and it is measured working on fabji's share: max
concurrent walks **8 → 2**, per file **6,8 → 0,8 min**, batch **7,3 → 3,4 min** (`Outsiders` S10, eight
episodes, `/media/synology`, `ParallelWorkers` 8, every job `UNVERIFIED` so nothing was written).

Two things keep it from being trustworthy, and both are in its own measurement path:

- **S32 — the instrument cannot say which case it is in.** `walk ceiling: holding <volume> at 2 concurrent
  media read(s)` picks its wording from the cap *value*, and 2 is both the unmeasured fallback and what a
  volume measured between 5 and 100 ms per read gets. On 2026-09-14 it printed "this volume has not been read
  yet" once a minute for a volume whose own extraction pass had measured **7,7–39 ms per read** minutes
  earlier.
- **S33 — a fast volume's ceiling may never lift.** `VolumeProfiles` is fed only by reads made through
  `ReadPolicy` (`ReadPolicy.cs:466 _volume?.Observe`), which come from the extraction path. So when extraction
  is served from the subtitle cache (`extract: method=cache … no read: this file's subtitle was extracted on an
  earlier run`) nothing feeds it, no samples are kept, and the conservative cap of 2 sticks — **on a fast
  volume as well**. And the reads that actually cost the time are invisible: ffsubsync runs as a child process,
  so the tens of thousands of reads a walk makes are never measured by the plugin's read path.

Together these are a throughput regression for anyone whose storage is *not* slow, introduced by a change meant
to protect people whose storage is slow. That is the wrong direction and it is why this goal exists.

## GOAL

Make the ceiling reflect what the volume has really cost, and make it lift when a volume is fast — with no new
setting, and without regressing fast setups.

## Work

1. **S32 first, because it is the instrument.**
   - The hold line must name the measured value and its unit when there is one, e.g.
     `walk ceiling: holding <volume> at 2 media read(s) — this volume measured 21,0 ms per read`, and say
     "nothing measured yet" **only** when the profile genuinely has no samples.
   - Keep the one-line-per-volume-per-minute rate limit, and make it readable from the line alone which rule
     produced the cap: unmeasured / measured slow / measured thrashing.
2. **S33: give the profile a second source of truth.**
   - Feed the volume **from the walk itself** when it finishes. The plugin knows the file's length and the
     engine's wall clock, so it can record an effective throughput for that volume without taking any read to
     measure it — `VolumeProfile` already carries both a latency and a throughput metric.
   - Decide the cap from **either** signal: measured read latency (from extraction reads) **or** measured walk
     throughput. A volume the walk says is fast must go uncapped even when every extraction was served from
     cache.
   - Keep "unmeasured" conservative. The fix must not reopen the hole 2.0.30 shipped: a volume nothing has
     measured yet stays at 2, and the first wave is planned against that.
   - Thresholds stay data-derived and documented (5 ms per read; 100 ms per read; whatever throughput figure is
     chosen), and the mapping is unit-checked at each of those numbers.

## Non-negotiables

- No new setting: the behaviour must be derived, never configured.
- **No regression for fast storage.** Once a fast volume has been walked once, three or more concurrent walks on
  it must be allowed.
- No change to sync behaviour: the extraction route, the alignment, and what gets written are untouched.
- Suite green after every step; one item, one commit; FIX_PLAN updated as the work happens, evidence quoted
  verbatim, `done` rows carrying the commit hash — the file's own upkeep rule, which now lives at its top.
- Hold before pushing or bumping a version, as always.

## Verification — what "done" means

- **Policy level**: the cap mapping at each threshold; an unmeasured volume stays at 2; a volume measured fast
  *by the walk* goes uncapped; a volume measured slow by either signal stays at 2; a volume that cannot be
  identified stays at 2.
- **Mixed-volume harness** (`tests/backend/`, the tmpfs + disk pair already in the suite): a cold store planned
  with eight heavy jobs admits 2; after a fast walk has been recorded for the fast volume, a wave on it admits
  more than 2.
- **Field, on fabji's server, quoting the log**:
  (a) the hold line names the measured number;
  (b) on the slow share, max concurrent audio walks ≤ 2 on the season test — **clear the speech cache first**,
      or the walks are skipped and the run proves nothing;
  (c) on a **local** volume, an audio-ruler batch shows **more than 2** concurrent walks once that volume has
      been walked. That is the test that proves the fast path is not throttled, and it has never been run.
- Before/after numbers in the FIX_PLAN row. Not adjectives.

## Failure modes this goal must not repeat

- **Shipping a cap whose input is never populated.** That is exactly what 2.0.30 did: the ceiling was asked
  before anything had fed it, answered "no ceiling", and eight walks went onto one share.
- **Making "unmeasured" mean "fast" again** anywhere, in order to make the fast path work.
- **Calling it fixed from unit checks alone.** The fast-volume lift must be seen in a field log.
