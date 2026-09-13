# Goal: one read policy for subtitle extraction — correct for every file and every storage

## Objective

Replace the three scattered read-window / prefetch decisions with a single policy that chooses one route
per extraction pass from (a) what the file's index actually provides and (b) what the storage is measured
to cost *during* the pass. Verify the result across the full matrix of file shapes and storage profiles, so
extraction cost is bounded by the bytes it needs and never pays twice — on any server.

## Done means (verified, not asserted)

1. **One policy, three callers.** A single component decides the read route. The cue-indexed path, the
   cluster walk and the shared multi-track pass all call it. A check fails if any of the old constants or
   window assignments is used outside the policy.
2. **The policy predicts, the log compares.** Each pass states its expected cost (bytes read, read calls)
   and the log prints expected vs actual side by side. A pass missing its own prediction by more than 2x
   logs a warning with the numbers.
3. **Route invariants, each with a regression check:**
   - no byte is read twice in one pass — prefetched ranges must cover the reads they were fetched for;
   - a read served from memory is never billed as a real read;
   - bytes read never exceed the file's size plus one window;
   - on cheap storage a cue-indexed pass reads < 20 % of the file (the 2.0.5 guard, kept);
   - on per-read-latency storage, reads per pass are bounded by bytes/window + prefetched ranges, never by
     cue count.
4. **Matrix checks in `tests/run_checks.py`**, all synthetic (no ffsubsync, no real media):
   cue offsets present / absent x 1 track / 32 tracks x four storage profiles — fast, 10 ms per read,
   11 MB/s, and both. The route chosen and the resulting cost are asserted per cell.
5. **Reproduced on the real fixtures.** The slow-profile numbers from the user's log (0,5 GB and 50-70 s per
   Arcane episode; 1,3 GB and 120 s for Jurassic Park) reproduce in the rig, then improve — with before and
   after numbers quoted in the release notes.
6. **Release gate green and unchanged:** `python3 tests/run_checks.py` exits 0, version bumped, changelog
   entry written, pushed to `beta`, and the catalog verified to serve the new version with a matching MD5 of
   the downloaded zip.

## Constraints

- **Read path only.** No change to sync logic, ffsubsync invocation, cache semantics, scheduling or UI.
- **No rewrite.** Keep the existing phases and their behaviour; move the *decision* into the policy.
- **Correctness is non-negotiable:** identical subtitles out for identical input, suite green before every
  push, including the sparse-fixture byte guards.
- Any change that cannot show a measured improvement in the rig does not ship.

## Failure modes to avoid (each of these cost a session)

- Deciding from a probe taken *before* the work: under concurrent load it reported 0,4 ms and 255 ms per
  16 KB on the same box within minutes.
- Validating a change with a check that cannot disagree with it — re-running the same configuration, or a
  threshold chosen by hand.
- Fetching ranges that do not cover the reads they were made for: 118 MB fetched, 2 282 real reads still
  issued, 70,9 s on a share that had just answered the prefetch.
- Fixing one path while another keeps the old behaviour, then reading the mixed outcome as the fix's result.
- Benchmarking on a rig that models only one axis of the storage (bytes) when the real one is slow at bytes
  *and* per read.
- Reporting a speedup derived from arithmetic instead of a measured run.

## Process

- One change per release; every release carries the rig measurement that justifies it.
- After each release, read the user's plugin log and compare predicted vs actual cost per pass. The log is
  the acceptance test, not the changelog.
- If a shipped change shows no improvement in that log, revert it before starting anything else.

## Where things are

- Repo: `/opt/data/jellysubsync` (branch `beta` → catalog
  `https://makeitmakesencethen.github.io/jellysubsync/beta/manifest.json`).
- CI: `.github/workflows/tests.yml` (checks) and `beta.yml` (build + publish).
- User's plugin log: `/subsync-logs/subsync.log` — fields worth reading: `extract: method=… ms=… cues=…
  bytesRead=… readCalls=…`, `extract lane: … reads … ms`, `cue window … -> … KB`, `dispatch: …`,
  `job … completed|UNVERIFIED|FAILED`.
- Rig: `tests/backend/slowread.c` (LD_PRELOAD shim) and `tests/backend/slowread-fabji.env`
  (10 ms per call + 1,46 ms per 16 KB = 11 MB/s, the user's measured share).
- Lessons already written down: `jellyfin-plugin-development` skill, §19 and §26.
