# PROGRESS — where the register work stands

Goal: `FINISH_EVERYTHING_GOAL_PROMPT.md` — close `knowledge/FIX_PLAN.md` without the user running anything.
Updated as work happens. The last item is the only one anyone needs to read to continue.

## Item 0 — the harness (done, commit `eed433a`)

`tests/rig/run_scenario.py` is the one command per scenario, `tests/rig/results.json` the file of evidence,
`tests/rig/README.md` the map. It starts a local Jellyfin with a named plugin build and a named storage
profile (the `slowread.so` shim in front of one media directory, plus a fast tmpfs volume), queues a batch
through the plugin's API, waits for the evidence in the plugin log and asserts on it.

    python3 tests/rig/run_scenario.py --list
    python3 tests/rig/run_scenario.py --scenario smoke --no-shim

Verified before that commit: `smoke` PASSES (both volumes, fixtures present, log present); the shim charges
231 ms per read on the shimmed volume and nothing on the fast one (4 reads = 0,93 s vs 0,0001 s, measured
with `dd`). Two harness bugs were found and fixed the same session — waiting for the first probe line instead
of both volumes, and reading a volume key out of the wrong token — which is why the first records in
`results.json` look thin.

## Item 1 — S41 (done: 2.0.38, register row closed)

One cold read could decide a volume's class and hold it to one walk for a whole run.

- **Reproduced** with `--scenario s41-cold-read`: the shim answers the first read on a freshly opened handle
  in 231 ms and every later one in 13 ms (new knobs `SLOWREAD_FD_FIRST_MS` / `SLOWREAD_FD_FIRST_READS`), which
  is the field's shape by construction. 2.0.37 answered `so it was read once: 16 KB took 231,13 ms - the
  ceiling for that volume is 1 (… which is thrashing)` and the scenario FAILED.
- **Fixed**: the probe takes three 16 KB reads at separated offsets, feeds all of them to the profile, logs
  `median of 3 reads` and the slowest sample, and a read-side verdict backed by fewer than two reads can no
  longer reach the thrash tier (`ThrashTierMinReads`). The same command now PASSES 6/6 with
  `median of 3 reads took 13,11 ms (slowest 81,14 ms) … ceiling … 2`.
- Accepted separately against the user's own measured share (`--scenario s41-steady`, 10 ms per read and
  11 MB/s): ceiling 2, fast volume `none`.
- Suite: 629 checks green (`python3 tests/run_checks.py`), including four new ones for this row; register
  linter PASS; `python3 tests/check_fixplan.py` now reports 7 open sections (S41 closed).

## Next, in the order the goal sets

1. **S39** — prove a ceiling chosen between two measured numbers, quoting the log line that names the volume's
   own walk against the best this machine has measured. The ratio machinery is implemented; the row still owes
   the proof. Needs a scenario in which two volumes of the same run produce walks at different throughputs
   (the shim on one, the tmpfs on the other), then a hold line that quotes both numbers.
2. **S40 + S7** — the enqueue's `log` phase costs 8-21 s per item. Diagnose what that phase writes before
   changing anything; the fix must show the same batch queued in a fraction of the time, measured.
3. Then B6, B8, B23, B13, F10, D3, the mediums in tier order, the lows, S30. **S31** stays parked on the
   user's decision.

## How to continue in one command

    python3 tests/rig/run_scenario.py --scenario smoke --no-shim        # the rig is alive
    python3 tests/run_checks.py && python3 tests/check_fixplan.py       # both gates green
    git log --oneline -3                                                # last item committed
