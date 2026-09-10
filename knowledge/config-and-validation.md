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
