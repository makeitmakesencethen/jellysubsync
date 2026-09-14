# GOAL: close the register — every open row either fixed with proof, or left open with a reason

The register (`knowledge/FIX_PLAN.md`) holds 52 open rows and 6 closed ones. The instruction is to work through
them without the user running anything: **the testing is mine to do, on this machine, and every claim in the final
report has to name the command that produced it.**

## Why this is possible at all

Because the instrument already exists here and is already documented — this is not a promise to invent one:

- **`/opt/data/tmp/backendtest/slowread.so`** — an `LD_PRELOAD` shim that puts a real per-read latency on one
  directory and nothing else:

      SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/     (default)
      SLOWREAD_MS_PER_16K=12.8                           (default)

  It intercepts `pread`/`read`/`readv`, resolves the descriptor through `/proc/self/fd`, and sleeps in proportion
  to the bytes returned — so a local NVMe stands in for the user's NAS without root, FUSE or a loop device, none of
  which exist in this container. Their share measures 12,8 ms per 16 KB read at rest; **the same knob at 231
  reproduces the S41 misclassification exactly**, which is the difference between arguing about a defect and
  demonstrating it.
- **`/opt/data/jf12test`** — a real Jellyfin instance (`dotnet /opt/data/jf12test/jellyfin/jellyfin.dll`), with
  `media/` (fast), `media-slow/` (shimmed, the stand-in share), `media-fixtures/` (embedded subtitle track, bitmap
  track, ASS track — the cases the register's S5/S19/F19 rows need), `log_*.log` and `stdout-{slow,fast,authz}.log`
  from runs already made through it.
- `/opt/data/.dotnet/dotnet`, `/usr/bin/ffmpeg`, `/usr/bin/ffprobe`, 76 GB free.
- `tests/run_checks.py` (625 checks, green) and `tests/check_fixplan.py` (the register linter).

So "the user has to test it" stopped being true the moment the harness got scripted. **Item 0 is that harness**, and
nothing else starts until it exists.

## Non-negotiables

1. **One item, one commit.** No bundling. A commit names the row it closes.
2. **Reproduce, then fix, then prove.** For every row: first make the harness (or a check) *show the defect*, quote
   that output in the register row, then fix it, then show the same observation passing. A fix whose evidence is a
   code reading and not an observation is labelled as such, in those words.
3. **Nothing is "done" without evidence in the row**: a quoted log line, a check name, a file:line, or a commit
   hash. `tests/check_fixplan.py` enforces the shape; it cannot enforce the truth, so that part is on me.
4. **The suite stays green.** `python3 tests/run_checks.py` before every commit, and `python3 tests/check_fixplan.py`
   too. A red suite is never committed, not even "temporarily".
5. **The register is updated as the work happens**, not at the end, with the work's own evidence and commit hashes.
   Rows that turn out not to be real are **closed as refuted, never deleted**, with what refuted them.
6. **Works on any machine.** No constant may be tuned to this box or to the user's NAS. Where a number has to
   exist, it is derived, or it is a documented fallback with its limits written down. The shim is how that is
   *tested*: the same scenario run at 2 ms, 12,8 ms and 231 ms per read must produce sensible ceilings at all
   three, and a "fast" volume at 2 ms must never be capped.
7. **No new settings** unless the row specifically needs one, and then the setting is documented in the register
   row and in the UI text.
8. **Nothing touches the user's server.** I cannot reach it and will not try. The plugin is published to the beta
   catalogue for them to install when they are back.
9. **History is never rewritten, `main` is never touched, no force-push.** The repository's `.git` is large and the
   user has explicitly declined a rewrite. One rebase on `beta` is the only history operation ever allowed.
10. **If a row cannot be verified here, say so and stop on it.** Implementing blind and calling it fixed is the one
    failure this whole exercise is meant to prevent.

## Item 0 — the harness (everything depends on it)

Under `tests/rig/` in the repository, scripted so a scenario is **one command** and its result is a file:

- `tests/rig/run_scenario.py` — start a rig with a named plugin build and an optional shim setting, queue a batch
  through the plugin's API, wait for it to finish, dump the plugin log, and assert on it. Arguments for: which
  media directories, how many files, the shim's `SLOWREAD_MS_PER_16K` (or none), the plugin build, the timeout.
- An API key for the rig, created by the script itself if absent (the rig's database is mine to write to; a key
  acts as the admin user, which is exactly what the plugin's endpoints check).
- Every scenario appends its result — scenario name, plugin build, shim setting, the assertions, the log lines that
  decided them — to `tests/rig/results.json`, so a long run survives an interruption and the final report can be
  built from the file rather than from memory.
- A `tests/rig/README.md` saying how to reproduce every claim in one line.

Verify the harness before anything else: **run the S41 scenario against the current build and watch it fail** —
a shimmed volume at 231 ms per read must come out `ceiling 1 (… thrashing)` while the fast volume comes out
`none`, which is production behaviour on their server reproduced here. That failing observation is the first
evidence in the register.

## Work order — blast radius first

1. **S41** (high, the cold read): reproduce at 231 ms per read; fix the probe to take several reads and use the
   median, print what the median stands on, and require more than one sample before the thrash tier can be reached;
   prove that the shimmed volume at 12,8 ms comes out at 2 and the fast one at `none`, in the same run.
2. **S39** (high): with S41 fixed, prove a ceiling chosen *between two measured numbers* — the reasoning in the log
   along the lines of `this volume's last walk moved X MB/s against a best of Y MB/s measured on this machine` — and
   close the row on that line.
3. **S40 + S7** (high): the enqueue's `log` phase costs 8–21 s per item. Diagnose what that phase writes before
   changing anything; the fix must show the same batch queued in a fraction of the time, measured by the harness.
4. **B6** a job can stay `Running` forever.
5. **B8** a failed ffmpeg extraction is accepted if a partial file exists.
6. **B23** `ClearStaleJobDirectories` recursively deletes anything under the scratch root.
7. **B13** per-reader memory (4 MB window + up to 384 MB of prefetched ranges).
8. **F10** no range checks on the numeric settings given to the engine as argv; **D3** settings validation partial.
9. Then the mediums in the register's own tier order: D5, D6, F29, B12, B14, B16, B17, B19, B20, B26, B3, B5, B7,
   F2, F6, F9, F14, F15, F16, F19, F20, F21, D8, D9, S5, D23 — and **S8**, which is a decision row: implement it
   only if the decision is unambiguous from the row's own wording, otherwise leave it for the user.
10. Then the lows: F1 (low only because it runs as the Jellyfin user — keep the finding honest), F7, F12, F17, F18,
    F22, F24, F25, F26, F28, F30, B15, B18, B21, B22, B27, B28, B29, B31, B16, S36, D12, F24.
11. **S31** (parked on the user's decision) and **S30** (a refusal rendered as `FAIL`): implement S30 if the status
    vocabulary allows it without inventing a UI contract; leave S31 parked, quoting the decision.

## Publishing, while the user is away

They asked me to finish, which means the beta catalogue should carry the work when they return, but every release is
gated: full suite green, the relevant harness scenario passing, versions bumped in `csproj` + `meta.json` +
`build.yaml`, a `CHANGELOG.md` entry, the published artifact verified (md5 against the manifest, DLL version, the
markers of the fix present), and the previous beta version left installable so they can pin it if the new one is
wrong. `main` is untouched. A rollback line goes in the release notes.

## Stopping rules

- Stop cleanly at any point: suite green, register accurate, no half-committed item, `tests/rig/results.json` and a
  short `PROGRESS.md` saying exactly where the work stands and what the next command would be.
- If a fix turns out to be bigger than the row implies, do not stretch the row: write a new row, say why, and move
  to the next item in blast order.
- If a scenario cannot be run here at all (it needs the user's real hardware, or a server feature this container
  lacks), the row says so in those words, with what would prove it, and stays open.

## The report the user reads

Plain, ranked, and checkable: what closed with which evidence, what stayed open with the reason, the plugin's
version history for the session, and for each claim the one command that reproduces it. No claim that is not backed
by a line of output I actually produced — not by a code reading, not by a plausible mechanism.
