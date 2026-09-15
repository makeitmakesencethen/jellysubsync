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

## Item 2 — S39 (done, register row closed; no release: rig and register only)

A volume was judged by constants measured on one machine; the row owed proof that the judgement is a ratio
between two numbers *this* machine measured, and it now has it.

    python3 tests/rig/run_scenario.py --scenario s39-ratio --timeout 600      # 7 of 7, 97 s

    walk ceiling: holding /opt/data|/dev/nvme0n1p2 at 2 concurrent media read(s) - this volume's last walk moved
                  1.3 MB/s against the best 162.7 MB/s this machine has measured (0.01x), which is storage-bound

One volume (overlay filesystem, a library the scenario registers itself) sets the bar with 12 walks; the judged
one walks once at 1,3 MB/s on the shimmed share; the hold line states both numbers and picks 2. The scenario had
to be sequenced: a hold only exists while a volume is *at* its cap (three judged jobs queued together, two run,
the third is held), and the judged volume needs a walk of its own first or the line falls back to the read tier —
both measured, both written down in `results.json`.

**Found while proving it, now S42 (medium, open):** the same file on the same shimmed volume "walked" at
280,1 MB/s with the audio analysis cached and 1,3 MB/s without it, because the walk figure is the file's length
divided by the engine's time and a cached run reads no media. Not fixed here — it is a decision for its own item.

## Item 3 - S31 (in progress; the user reordered the register around correctness and data-safety)

Reproduced end to end on the rig, then partly fixed - and the fix the row proposed is refuted by measurement.

- **Reproduction** (`python3 tests/rig/run_scenario.py --scenario s31-wrong-ruler`): a sibling embedded track from
  a different cut (the same track stretched 1,02x) passes every check and makes the plugin write a wrong subtitle
  with a *note* and a `Completed` status - ~29 s wrong, silently. Released 2.0.38, quoted in the register.
- **Implemented**: the engine's own `score:`/`offset seconds:` are captured and logged for both reference paths;
  `SyncChange` carries the per-cue dispersion and `RulerSpreadTooWide` refuses a ruler whose cues did not move
  together. 638 checks green, nine of them new.
- **Refuted**: "refuse when the score is far below what the audio path produces" does not discriminate - the wrong
  ruler in the rig scored 66 451 against the same file's audio 53 566, and in a direct test an other-cut ruler
  scored *higher* (274 721) than the correct one (198 713). Numbers in the register.
- **Next, and the shape S31 should close on**: cross-check a subtitle ruler's answer against the film's own audio
  when its demand is suspicious (the note above is the plugin saying "this looks wrong" and doing nothing). One
  audio analysis, cached, for the suspicious case only.

Hold for go-ahead before shipping, as instructed: the work is committed locally, not pushed.

## Then, in the register's order

1. **D3 + F10** — range/format validation on settings, before they are saved and before they reach the engine.
2. **B8** — a failed ffmpeg extraction must not be accepted because a partial output exists.
3. **B23** — `ClearStaleJobDirectories` needs a scoped delete or a root-path safety check.
4. **B6** — a job stuck `Running` needs the heartbeat/deadline from S27.
5. Then **S40 + S7** (the enqueue's `log` phase) and the rest of the performance tier, **S42**, the mediums, the
   lows. **S31**'s cross-check above comes before all of them.

## How to continue in one command

    python3 tests/rig/run_scenario.py --scenario smoke --no-shim        # the rig is alive
    python3 tests/run_checks.py && python3 tests/check_fixplan.py       # both gates green
    git log --oneline -3                                                # last item committed
