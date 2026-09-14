# The rig — one command per scenario, one file of evidence

This is where a claim in `knowledge/FIX_PLAN.md` stops being prose. A scenario starts a real Jellyfin
on this machine with a named plugin build and a named storage profile, queues a batch through the
plugin's own API, and asserts on what the plugin logged. Every assertion that ran — passed or failed —
is appended to `results.json` with the log lines it decided on.

Nothing here touches fabji's server. It is a local stand-in for it.

## One command per scenario

```sh
python3 tests/rig/run_scenario.py --list                                  # what exists, and what each needs
python3 tests/rig/run_scenario.py --scenario smoke --no-shim              # the harness checking itself
python3 tests/rig/run_scenario.py --scenario s41-thrash-tier --ms-per-call 231
```

- **Exit 0** = every assertion held. **Exit 1** = at least one did not, and the failing line quotes the
  observation that failed it. A scenario that cannot fail proves nothing, so this is the intended use:
  run it against an unfixed build and read the defect out of the output.
- `--ms-per-call` is the shim's charge per read (a share that charges per round trip);
  `--ms-per-16k` is the charge per 16 KB (11 MB/s = `1.46`). Both together is fabji's share:
  `--ms-per-call 10 --ms-per-16k 1.46`. S41's field number is `231`.
- `--keep-rig` leaves the server up for inspection; `--plugin-version` installs a named build instead
  of the current tree; `--timeout` bounds the wait for evidence.

## What it runs against

| Volume | Path | Volume key | Why it exists |
|---|---|---|---|
| slow (shimmed) | `/opt/data/jf12test/media-slow` | `/dev/nvme0n1p2` | `LD_PRELOAD=slowread.so` puts a real per-read and per-byte cost on this directory only |
| fast | `/dev/shm/s39-fast` | `shm` | a tmpfs: a genuinely fast *volume*, not a fast directory on the slow one |

They must be different filesystems. The plugin names a volume by the device behind the longest mount
point containing the path (`Services/VolumeProfile.cs`), so `media/` and `media-slow/` — both on
`/dev/nvme0n1p2` — are **one** volume and produce **one** verdict. A scenario comparing two verdicts
must use two filesystems.

The fixture both volumes get is `media/Embedded Test (2026).mkv` (248 KB, two embedded subrip tracks):
small enough that a queue at 231 ms per read is still seconds, real enough that the extractor takes its
cue-indexed route. It is hardlinked onto the disk volume and copied onto the tmpfs.

## The shim

`tests/backend/slowread.c` → `slowread.so`, built automatically if absent. It intercepts
`pread`/`read`/`readv`, resolves the descriptor through `/proc/self/fd`, and sleeps in proportion to
what came back, but **only** for paths under `SLOWREAD_PREFIX`. So one local NVMe plays both a fast
volume and a share, in the same run, with no root, no FUSE and no loop device (none exist in this
container). `tests/backend/slowread-fabji.env` holds fabji's measured numbers as a comment.

## Why the caller is the server's own admin, not an API key

`rig.py` reads the admin's access token out of the rig's `Devices` table. Since **2.0.34** the plugin's
item endpoints resolve *who is calling* and fail closed without an identity (`F3`), so an API key would
test a door no user walks through. A token is the stricter instrument, and the one that can disagree
with a fix.

## Results

`tests/rig/results.json` — one record per run: scenario, when, plugin build, shim numbers, repo head,
each assertion with its verdict and the line that decided it, the probe/ceiling log lines verbatim, the
batch id, and the log tail. Appended, never rewritten, so an interrupted run still holds its evidence
and the report is built from the file rather than from memory.

## Adding a scenario

Write one function `def scenario_x(rig, args, ctx)` that returns a list of `(check, passed, detail)`
tuples, register it in `SCENARIOS` with a one-line `needs`, and add the command here. Use
`rig.wait_for_log(pattern, since, timeout)` to wait for the evidence rather than for a batch to finish:
most rows are decided by a plan or a verdict that appears long before the work ends.

`tests/backend/README.md` documents the older, wider harness (`drive.py`, `rows_*.py`, the 2026-09-11
matrix); this directory holds the scenario runner that the register works through.
