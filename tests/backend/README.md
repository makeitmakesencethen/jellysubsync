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
