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
