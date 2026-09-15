# Configuration & Validation

## Plugin Configuration Fields

Source: `Jellyfin.Plugin.SubSync/Configuration/PluginConfiguration.cs`

| Field | Type | Default | ffsubsync Flag | Notes |
|-------|------|---------|-----------------|-------|
| `FfSubSyncPath` | `string` | `"ffsubsync"` | — | Empty/"ffsubsync" = bundled linux-x64 binary (extracted on first start); set to override |
| `FfmpegPath` | `string` | `""` | `--ffmpeg-path` | Empty = system PATH |
| `VadMethod` | `string` | `"subs_then_webrtc"` | `--vad` | Validated against C# allow-list |
| `MaxOffsetSeconds` | `int` | `60` | `--max-offset-seconds` | HTML input: min=1, max=600 |
| `MaxSubtitleSeconds` | `double` | `10.0` | `--max-subtitle-seconds` | HTML input: min=1, max=60, step=0.5 |
| `OutputEncoding` | `string` | `"utf-8"` | `--output-encoding` | Validated against C# allow-list |
| `UseGoldenSectionSearch` | `bool` | `false` | `--gss` | |
| `OverwriteExisting` → `SyncModeCopy` | `bool` | `true` | — | true = write new `-SYNCED` sidecar copy (original untouched); false = replace the original in place (backup + rollback) |
| `SweepFailStreakLimit` | `int` | `3` | — | Library sweep: consecutive execution failures before a subtitle stops being retried; content change resets the streak |
| `SweepMaxItemsPerRun` | `int` | `500` | — | Library sweep: max subtitle tracks queued per run |
| `ExtractionTimeoutMinutes` | `int` | `20` | — | How long one embedded-subtitle extraction may take before it is aborted with a clear error; clamps to 1-240 |
| `StuckJobTimeoutMinutes` | `int` | `15` | — | How long a running job may show no activity at all while no process of its own is running before it is stopped so its run can continue; clamps to 2-240 (B6) |
| `WedgedProcessTimeoutMinutes` | `int` | `60` | — | How long a process that is still running for a job may print nothing before that job is treated as wedged; clamps to 5-1440 (B6) |

## C# Allow-Lists (Argument Injection Prevention)

Source: `SubSyncService.cs:130-139`

**Allowed VAD Methods** (subset of ffsubsync's own `--vad` choices):

| Value | In HTML form? |
|-------|---------------|
| `subs` | No (legacy alias, accepted) |
| `webrtc` | Yes |
| `subs_then_webrtc` | Yes |
| `auditok` | Yes |
| `subs_then_auditok` | Yes |
| `subs_then_silero` | Yes |
| `silero` | Yes |

**Allowed Output Encodings:**

| Value |
|-------|
| `utf-8` |
| `ascii` |
| `latin-1` |
| `utf-8-sig` |
| `utf-16` |

## VAD Allow-List: HTML and C# in Sync

The C# allow-list (`AllowedVadMethods`) contains every method the configuration forms
offer, so no configured value can fall back silently. Every engine-side choice the
forms expose (`webrtc`, `subs_then_webrtc`, `auditok`, `subs_then_auditok`,
`subs_then_silero`, `silero`) was confirmed against the bundled ffsubsync 0.5.1
`--help` output. If a future engine version removes a choice, trim BOTH the C# set and
both HTML forms together — a client/server mismatch here is a silent-default bug.

## ffsubsync Binary Resolution Order

Source: `SubSyncService.cs:175-195`

1. User-configured custom path (explicit override — wins even over the bundled binary)
2. Bundled binary: `{PluginFolder}/ffsubsync/{rid}/…` (zero-setup default when shipped)
3. Managed venv binary: `{VenvPath}/bin/ffsubsync`
4. System PATH fallback: `"ffsubsync"`

## Managed Virtualenv Paths

Source: `SubSyncService.cs:158-168`, `Plugin.cs:54-59`

| Component | Path |
|-----------|------|
| Venv root | `{DataPath}/subsync/venv` |
| ffsubsync binary | `{VenvPath}/bin/ffsubsync` |
| pip binary | `{VenvPath}/bin/pip` |
| python3 binary | `{VenvPath}/bin/python3` |
| Temp working dir | `{CachePath}/subsync/{jobId}` |

## Argument Escaping

Source: `SubSyncService.cs:1530`

The engine no longer hand-escapes arguments. ffsubsync (`BuildFfSubSyncArgs`) and the
ffmpeg paths both build `List<string>` and pass argv via `ProcessStartInfo.ArgumentList`.
`EscapeArg` survives only on the legacy managed-venv install path (python/pip
invocations through the string-argument `RunProcessCaptureAsync`); if that path is
ever removed, delete `EscapeArg` too.

**Not handled**: trailing backslashes (can break trailing-quote), newlines, tabs,
shell metacharacters (`$`, `` ` ``, `&`, `|`, `;`, etc.).

The project does not use `ProcessStartInfo.ArgumentList` (which handles escaping
correctly) — arguments are built as a single `Arguments` string via `string.Join(" ", args)`
at `SubSyncService.cs:887`.

## Build Constraints

Source: `Jellyfin.Plugin.SubSync.csproj:5-6`, `Directory.Build.props`

- `TreatWarningsAsErrors=true` — all compiler warnings are build errors
- `GenerateDocumentationFile=true` — all public members require `///` XML doc comments
- `Nullable=enable` and `ImplicitUsings=enable` set via `Directory.Build.props`

This means any PR adding undocumented public/internal members will fail to build.

See also: [architecture-and-gotchas.md](architecture-and-gotchas.md) for build constraints context.

## Job Cleanup Logic

Source: `SubSyncService.cs:1123`

The cleanup timer fires every 30 minutes. Actual eviction behavior (from `CleanupOldJobs()`):

- **Failed / Cancelled jobs**: always evicted on the next cleanup run, regardless of age
- **Completed jobs**: evicted when either (a) finished more than 1 hour ago (`cutoff`,
  `DateTime.UtcNow.AddHours(-1)`, **is** used — it is not dead code), or (b) total job
  count exceeds 50 — in which case **all** completed jobs are evicted at once rather
  than trimming down to a target size
- **Queued/Running jobs**: never evicted

Jobs live only in memory (`_jobs`), so everything is lost on a Jellyfin restart; the
cleanup rules only bound the lifetime of an otherwise unbounded in-memory list.


## Stopping jobs that stop making progress (B6)

Source: `Services/StuckJobPolicy.cs`, `Services/JobProcessRegistry.cs`, `SubSyncService.ReapStuckJobs()`

A job left in `Running` holds its worker slot and keeps its batch unfinished, so nothing else can finish and the
only way out was a restart. Two independent halves close it:

- **Every exit settles the job.** `RunSyncJobWithContext` calls `StuckJobPolicy.Settle` in a `finally`, so a job
  that returns - or throws, including the throw that used to happen before the job's own error handling was
  reached - is failed with the reason and the last phase it reported rather than left `Running`.
- **A watchdog stops jobs that stop making progress.** `ReapStuckJobs()` runs on every scheduler pass and from the
  30-minute cleanup timer (so recovery does not depend on the pump being alive), throttled to one look every 20
  seconds. It judges each `Running` job from what was observed, never from a constant:

  * **no child process of its own and no activity for `StuckJobTimeoutMinutes`** → stopped. This is a wait that
    was never released, a reader on a share that stopped answering, or a task that returned without settling;
  * **a live child process that has printed nothing for `WedgedProcessTimeoutMinutes`** → stopped. A live process
    is *never* judged by the job's own clock: a demux of a large episode over a share, or a feature film's audio
    analysis (measured 91 minutes), is real work with quiet stretches - the bundled engine printed a line every
    ~0,5 s while it worked (47 lines in a 17,7 s run), against 5 minutes of engine silence inside a real
    6,7-minute run on this server's log;
  * **a job waiting on another subtitle of the same file** (the file's audio is analysed once; the rest wait on its
    gate) is spared for as long as that job is working - by its own activity or by having a live process.

  `JobProcessRegistry` is what makes the first two distinguishable: it ref-counts the child processes started for
  each job and dates each job's silence from the last stderr line a process produced, or from the process's own
  start when it has never printed one.

A stopped job is failed with the measured numbers (`Stopped because nothing has been running for it and it has
shown no activity for 16 min (Extracting subtitle…). Nothing was written for this subtitle; the rest of the run
continues.`), its token is cancelled so whatever it was waiting in unwinds and its slot comes back, and the
cancellation handler leaves it alone - a stop is not a user cancellation and is never relabelled as one. The
audio-analysis gate is awaited with the job's token for the same reason: a job parked there has to be reachable by
the user's Kill as well as by the watchdog.

Residual, deliberate: a job whose engine or extraction process is alive *and* printing is never stopped, so a
legitimate long analysis cannot be broken. A job whose task ignores cancellation keeps its slot, but its state is
terminal, so its batch and the interface are no longer waiting for it.

## Judging an extraction before it is used (B8)

Source: `Services/ExtractionOutputGuard.cs`, `SubSyncService.ExtractSubtitleWithProgressAsync()`

The ffmpeg fallback is the last resort for a track no index reader could produce, and its output is what gets
synced, so a partial extraction is silently wrong in the way that matters (cues missing, the tail of the film
untimed) and the engine cannot tell it from a complete one. The rule this replaced accepted a failed run whenever
a file existed at the output path; the more dangerous shape does not fail at all:

    ffmpeg reading a container cut short to 57 %: exit 0, 17 of 30 cues, a well-formed SRT that ends at a cue
    boundary, and the truncation on stderr only - "File ended prematurely"

So every extraction is judged on four things, and a refusal fails the job with the measured reason *and* deletes
the partial:

1. the exit code is 0;
2. ffmpeg's own log carries no container-level truncation marker (`ended prematurely`, `Truncating packet`,
   `Packet corrupt`, `Invalid data found when processing input`, `Error opening input`);
3. the file exists and is non-empty;
4. it is a structurally complete SRT: every block is a cue (number, timing line, at least one text line), at least
   one cue exists, and the file ends at a cue boundary - ffmpeg's SRT muxer always writes a blank line after a cue,
   so a file that stops inside one is a prefix, which is what a kill leaves.

Video-decode complaints (`error while decoding`, `corrupt decoded frame`) are deliberately not refusals: they
cannot lose subtitle cues and failing a job over one hands the user a problem they cannot act on. An extraction
that was stopped (kill, cancellation, `ExtractionTimeoutMinutes`) deletes what it wrote before it unwinds, so a
partial cannot sit in the file's shared extraction directory for a later job to read.

## SyncLanguages (global subtitle language filter)

`PluginConfiguration.SyncLanguages` (`string[]`, default empty) lists the languages that
may be synced. It is enforced in one place — `SubSyncService.ListSubtitles()` — which both
the UI (library browser, detail page, language dropdown) and the scheduled sweep read, so
a filtered-out track is never listed or queued. `SubSyncService` repeats the check when a
job actually starts as defence in depth against stale clients.

Semantics (see `Services/LanguageSupport.cs`):

- empty array → every text subtitle language is allowed
- comparison is done on normalised codes: `sv` / `swe` / `sv-SE` / `Swedish` are the same
  language, as are `chi` / `zh` / `zho` / `Chinese`
- tracks with no language at all (`und`) are skipped while a filter is set, because they
  cannot be matched by language
- image-based codecs (`pgs`, `dvd`, `vob`, `dvb`, `xsub`, `hdmv`, `bitmap`) are removed
  from the listing for every configuration — they can never be aligned

Both the dashboard Settings tab (chip list with add/remove) and the plugin config page
(comma-separated field) write this setting.


## MultiSyncMode / ParallelWorkers (multi-subtitle modes)

`MultiSyncMode` (`string`, default `normal`, allow-list `normal|parallel|fast|ultimate`) is
resolved per job through `SyncJobMode.Normalize`; `ParallelWorkers` (int, clamped 1-8,
default 4) applies to the parallel modes.

`SyncJobMode` owns the rules: `IsParallel` is true for `parallel` and `ultimate`,
`UsesSpeechCache` is true for `fast` and `ultimate`, so `ultimate` is exactly "parallel
plus reuse".

Wave selection lives in `SubSyncService.SelectWave` (public so the harness can exercise
it): same mode, same batch, at most one job per media file, up to the worker limit. The
one-file-one-slot rule means two workers never read the same file — without it they would
repeat the same audio analysis and, in the caching modes, race for one cache entry.

- **normal** — the pump takes one queued job at a time, ffsubsync analyses the audio on
  every run.
- **parallel** — the pump takes up to `ParallelWorkers` queued jobs *of the same batch and
  same mode* and runs them with `Task.WhenAll`; batches still never interleave, so FIFO
  order between batches is preserved.
- **fast** — before the run, `SpeechCache.KeyFor` builds a key from the media path, size,
  mtime, the VAD method and the ffsubsync binary, and the speech analysis is reused:
  - cache hit → the `.npz` is passed to ffsubsync *as the reference instead of the video*,
    so no audio work happens at all;
  - cache miss → ffsubsync runs with `--serialize-speech`, which makes it write the `.npz`
    next to the reference it was given. We therefore hand it a symlink inside
    `SpeechCache.Root` (`<state>/speech-cache`), so nothing is ever written into a media
    folder; if symlinks are unavailable the real path is used and `SpeechCache.Harvest`
    moves the produced `.npz` into the cache afterwards.
  - a cached run that fails (stale/truncated `.npz`) is retried once from the audio, after
    deleting the bad cache entry.

Measured (15-minute test media, bursts placed at the cue times): full run 2.98s vs 1.00s
with the cached speech; the `.npz` is ~2 KB. Results were byte-identical between the audio
path and the cached path for subtitles at three different offsets.


### Speech cache lifecycle

`SpeechCache.Root` is `<state>/speech-cache` (`Plugin.StatePath`), so entries live under
Jellyfin's plugin data directory and never in a media folder. Each entry is one `.npz`
holding the detected speech segments — kilobytes, not audio (measured: 1233 bytes for a
4-minute test file, 2004 bytes for a 15-minute one; it scales with the number of speech
segments, so a talky 2-hour film stays in the tens of KB).

- Written on a fast-mode cache miss (`--serialize-speech` against a symlink in the cache).
- `Prune()` runs after every new entry: first anything older than 30 days (write or access
  time), then oldest-first until the total fits in 250 MB. Orphans left behind when media
  is re-encoded (new key) are removed by the age rule.
- `Clear()` backs the Settings "Clear cache" button and the `POST SubSync/SpeechCache/Clear`
  route; size is reported as `SpeechCacheSummary` in `GET SubSync/InstallationStatus`.
- Temporary symlinks are deleted after each run (`DropLink`), so the directory holds only
  `.npz` entries.


## Parallel progress surface

`GET SubSync/Batch/{id}` returns `Mode`, `RunningTasks` (every task in `Running`, ordered by
batch position) and the existing `CurrentTask`/`Tasks`. The dashboard renders one row per
running task — title, phase, per-task progress bar — whenever the batch mode is `parallel`
or `ultimate` (or when more than one task is running), above the overall progress bar.


## Configuration migration

`PluginConfiguration.ConfigVersion` marks which defaults an install has been moved onto.
`Plugin.Migrate()` runs from both the constructor and `UpdateConfiguration`, so the
migration applies at load time (and is persisted immediately) as well as on save.

- Revision 1 (added with the automatic strategy): any stored `MultiSyncMode` is set to
  `auto`. Before that release there was no auto mode, so every stored value was either a
  legacy default or a manual pick; the automatic strategy resolves to the same or better
  behaviour for each run shape. Modes chosen after the migration are honoured.
