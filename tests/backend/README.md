# Backend test harness (2026-09-11)

The harness behind `knowledge/backend-test-report-2026-09-11.md`. It drives the **live** plugin
against a real Jellyfin server — it is not a unit-test suite, which is why it is not wired into
`tests/run_checks.py`.

## What is here

| File | What it does |
|---|---|
| `ss.py` | API client (server, token, plugin log, cache paths) + `record()` for measurements |
| `drive.py` | run one sync job or a batch and capture wall clock, job record and log slice; `orphan_check()` |
| `rows_a.py` / `rows_c.py` / `rows_d.py` | matrix sections A (modes x kinds), C (sources of subtitle text), D (settings) |
| `slow_baseline.py` | the aggregate run: one 50-track episode in a batch, per `ParallelWorkers` setting |
| `make_fixtures.py` | builds the 12 fixtures out of a 120 s cut of the real episode |
| `make_vobsub.py` | writes a VobSub (.idx/.sub) by hand — ffmpeg has no bitmap subtitle encoder |
| `ebml.py` | in-place EBML surgery (rewrites an element as a Void, offsets unchanged) |
| `slowread.c` | the slow-storage profile: an LD_PRELOAD shim, see below |
| `browser-item.js` | real-Chromium pass over the detail page (needs the Playwright install, see below) |
| `evidence-2026-09-11.jsonl` | every measurement row the report cites, one JSON object per line |

## Environment it assumes

* Jellyfin 12 at `127.0.0.1:8096`, data at `/opt/data/jf12test`, fixtures in
  `/opt/data/jf12test/media-fixtures` (library "SubSync Fixtures").
* An admin token in `/opt/data/tmp/jf_token.txt`.
* **The server only starts with `LD_LIBRARY_PATH=/opt/data/local/icu/usr/lib/x86_64-linux-gnu`** —
  this host has no system libicu, and `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1` is not a substitute
  (Jellyfin then fails with `CultureNotFoundException: en-US`).
* Paths are hard-coded to the paths above; there is no configuration layer.

## The slow-storage profile

There is no `/dev/fuse`, no `fusermount` and no root here, so a throttled block device or FUSE
filesystem was not available. Instead:

```sh
gcc -shared -fPIC -O2 -o slowread.so slowread.c -ldl
# hardlink the media into a directory of its own, add it as a library, then:
LD_PRELOAD=/path/slowread.so SLOWREAD_PREFIX=/opt/data/jf12test/media-slow/ \
SLOWREAD_MS_PER_16K=12.8 <jellyfin start>
```

It intercepts `pread`/`pread64`/`read`, resolves the descriptor through `/proc/self/fd` and sleeps
proportionally to the bytes returned, **only** for paths under `SLOWREAD_PREFIX`. Calibration on one
2.38 GB file: 0.25 ms/read without it, 13.10 ms/read with it at `12.8`. The plugin measures it too:
`extract: storage 12.94 ms per 16 KB read -> walk window 4096 KB`.

## Driving the GUI

```sh
node browser-item.js    # needs require('/tmp/audit/pw/node_modules/playwright') to resolve
```

## Added 2026-09-11 (session 2)

| File | What it does |
|---|---|
| `start-server.sh` | starts the test server the only way that works here (`LD_LIBRARY_PATH` for libicu, `JELLYFIN_*_DIR` under `/opt/data/jf12test`); `SLOW=1` starts it under the slow-storage shim and builds `slowread.so` if it is missing |
| `s11_reference.py` | S11: queues a batch on the 2.38 GB episode and watches the reference tree and the plugin/server logs while it runs. Before: 7 Completed + 1 Failed (`unable to read reference`), the reference deleted with jobs still running; after: 8/8 Completed and the tree removed only when nothing was running |
| `s3s4_refusal.py` | S3 (a reference-derived shift past the limit) and S4 (a job that produces no subtitle text): job status, plugin log line, and that nothing in the library changed (sha256 before/after, no 0-byte sidecar) |
| `acceptance.py` | A4/A5/A6 runs: every track of an episode/series/season, then statuses, wall clock, empty or missing outputs, cue counts against the extracted set, cache leftovers, alive engine processes |
| `s11b_cancel.py` | S11b: cancels a batch while the engine's own child is running (`engine-cancel`) or kills the plugin mid-extraction (`lane-kill`) and reports what survived. **Not yet measured** — see the report |

Facts learned the hard way, worth not rediscovering:

* **`MediaStream.Index` is not stable.** Adding a sidecar to a library item renumbered this episode's
  subtitles from 4..54 to 8..57, so a hard-coded index silently syncs a different track. Select the
  track by language/forced flags, or re-read `/SubSync/Subtitles/{id}` immediately before queueing.
* **`/SubSync/Sync` answers with the job record itself** (`{"Id": …}`), not `{"JobId": …}`.
* **S11b's harness counts processes, so it must not count its own.** The first version matched any
  process whose command line mentions the media file, which included the shell running the `grep`
  that had started it and a manual `ffmpeg -f null -` calibration: the smoke run recorded those as
  "engine children" and a 70.9 s wall clock for a job that had finished in a second. It now requires
  the process to *be* `ffmpeg`/`ffprobe`/`ffsubsync` (`python …/ffsubsync` counts). S11b is still
  **unmeasured** — the smoke run proved the detector, not the defect.
* **The slow profile must be proved, not assumed**: the plugin logs `extract: storage <ms> ms per
  16 KB read`, and `0.01 ms` means the shim never loaded.
* `/SubSync/Subtitles/{id}` hides the plugin's own `.SYNCED.` sidecars, but `/SubSync/Sync` accepts an
  index that resolves to one and synced it into `X.SYNCED.ukr.SYNCED.srt`. The junk file was removed;
  the listing/sync mismatch is not fixed yet and is written up as a finding.
