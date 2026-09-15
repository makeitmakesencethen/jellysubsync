## 2.0.49 (beta)

Four interface fixes, all measured in a real browser or against a running Jellyfin 12 rather than reasoned about.

**D7 — an idle settings page stopped hammering the server.** It polled every 2 s with nothing running, and asked
the same question several times when it loaded. Measured before: about 1.4 requests per second on an idle page.
Measured after: **4 requests in 20 seconds (0.20 req/s)** — one tick per 10 s — and the load window went from
7 requests (three of them the same `/SubSync/Batches`, two inside 500 ms) to 5 with **no duplicate**. The poll is
self-scheduled (10 s idle, 2 s while a run is on screen), GETs in flight share one request, a fresh answer is
reused for 500 ms and any write throws that reuse away, and a hidden tab still skips its tick and catches up when
it comes back.

**D9 — the same subtitle can no longer be queued twice.** Asking for the same item and track now queues one job:
the first request answers `total=1, alreadyQueued=0`, the second answers `total=0, alreadyQueued=1` (in a batch of
its own), the first batch still holds exactly one task, and the log names the duplicate it refused. A *finished*
job is not a duplicate, so syncing a track again later still works, and a library sweep whose items are already
queued reports "nothing new" instead of counting them as failures. The page says so too.

**D10 — one error shape for every failure.** Refusal by a check and an exception that escapes an endpoint now
answer the same way: `{ status, title, detail }`. Thirteen places that answered with a bare string were converted,
and `SubSyncExceptionFilter` covers whatever is thrown (409 for a state conflict, 400 for an argument, 500
otherwise). The page reads `detail` instead of showing raw JSON. An empty batch answers "Empty batch: A batch must
contain at least one task."; an item the account cannot see answers "Not permitted: One or more items in this
request are not available to this account."

**D18 — where the page lives under Jellyfin 12, verified.** The installed client picks a plugin's *representative
page* with `EnableInMainMenu` and does not build sidebar entries from plugin pages, so no server-side flag can put
the plugin in the main menu — the main menu was measured and holds no plugin entry. What the plugin controls now
works: the flag sits on the settings page itself, the page is served at
`/web/configurationpage?name=subsync-main` (HTTP 200, 31 598 bytes, rendered), and the dashboard page both lists
the plugin and links there.

Verification: `python3 tests/run_checks.py` passes (823 checks, 0 failures) with 21 new ones, plus two probes that
run against a real Jellyfin: `tests/backend/d_series_probe.py` (10 checks: the two refusals, the duplicate
refusal end to end, both page routes) and `tests/gui/d7-d18-probe.js` (the poll rate and the main menu in a real
browser). Both probes pass in full.

## 2.0.48 (beta)

Six fixes from the B series, where the plugin either said something it had not measured or did nothing when asked.

**B19 — the progress bar no longer lies about how far it has read.** ffmpeg prints the same moment three ways, and
`out_time_ms` is *microseconds* despite its name — measured with ffmpeg 7.1 on this project's own fixture:
`out_time_us=33000000`, `out_time_ms=33000000`, `out_time=00:00:33.000000`. Dividing the middle field by 1000
turned 33 seconds into 33 000, which the extraction window clamps to "100 % of the file read", so a pass
alternated between the true fraction and a full bar once per progress block. It is read as microseconds now.

**B23 — the scratch clear only deletes job scratch.** `ClearStaleJobDirectories` recursively deleted every
directory in the cache root that was not named `ref` and not a tracked job, i.e. anything else that lived there;
with a misconfigured root that is data loss outside the plugin's own scratch. It now deletes only a directory
whose name is exactly a job id (32 lowercase hex characters) and only when the resolved path is inside the
resolved root, and it logs the refusal when it declines.

**B16 — the kill report counts processes that exited.** The number returned, logged and shown was
`Math.Max(processesKilled, runningKilled)` — the larger of two unrelated figures, neither of them an exit — so
"N stopped" was reported for processes still alive, after a `Thread.Sleep` poll loop. Each tree is now awaited on
its own handle with a deadline and the count is what actually exited, with survivors reported separately.

**B7 — Kill interrupts a Matroska walk.** The cue-indexed loop and the shared pass already checked the token per
cue point; the metadata walk checked it only every 64th cluster and read a fresh 16 MB chunk without asking. Both
now check at every cluster boundary. Measured on a purpose-built 1 200-cluster fixture: a pass cancelled from its
first progress line returns `reason=cancelled` with no text, having walked **401 of 1 200 clusters in 7 ms**.

**B17 — a run keeps its own history.** Completed jobs were evicted the moment the store passed 50 entries, so a
batch's beginning vanished while the user watched it (their own run was 2 497 tasks). Retention is age-bound
(an hour) with a 10 000-row backstop that drops the oldest first, and the rule is a testable policy
(`JobsToEvict`) rather than an inline query.

**B2 — a shared pass names the tracks it could not produce.** `MkvExtractionStats.MissedTracks` lists every
requested ordinal the pass did not produce, the reason names them (`no subtitle for track(s) 9`), the lane's log
line carries `missedTracks=…`, and the job's extraction note says so — a gap in a pass can no longer be read as a
track that was simply never queued for.

Verification: `python3 tests/run_checks.py` passes (802 checks, 0 failures) with 22 new ones — the three measured
progress fields and their fraction, the scratch-name and root-containment guards, a killed process counted as
stopped and a running one not, 60 fresh completed jobs surviving a cleanup pass plus the backstop, all three
cancellation cases (pre-cancelled, cancelled mid-walk, and that it stopped at a cluster boundary), and the
missing-track naming in both stats and reason — plus 6 source checks pinning the wiring.

## 2.0.47 (beta)

Four small things in the settings, all of the same shape: a control that said something the server did not do.

**The output encoding is chosen, not typed (F11).** The field was free text, and the server had always replaced a
value the engine cannot be given with utf-8 and said so afterwards - so a typo like `utf8` was invited, and the
report arrived as a footnote after a save that otherwise looked complete. It is now a dropdown over exactly the
five encodings the engine accepts (utf-8, utf-8-sig, utf-16, latin-1, ascii), each labelled with what it is for,
so the value cannot be typed wrong at all; the server-side replacement stays as the backstop for a hand-edited
config.xml and is still named on save. Verified against a real server as well: a chosen encoding comes back on a
fresh read (what the page's reload depends on), and `utf8` is replaced with
`Output encoding: 'utf8' is not one of ascii, latin-1, utf-16, utf-8, utf-8-sig; utf-8 is stored`.

**Golden-section search is no longer a switch that can do nothing (F12).** It only ever reaches the engine
together with framerate correction, so while "Correct framerate mismatch" is off the control is now disabled and
its row reads as inert (55 % opacity, `cursor: not-allowed`), on load and on every change of that switch, with
the description saying why - and the tick itself is kept, so turning correction back on restores what was set. A
hand-edited config.xml that pairs them the wrong way is told as well: "it is stored but the engine is not given
--gss".

**A saved worker count reaches the extraction lanes at once (F13).** The lanes' width is half the worker count
(capped at 3), and it was read from the plugin's in-memory configuration - which a settings file edited outside
the API never updates - while the sync side read the settings file. Both now read the same source, and saving a
configuration wakes the scheduler, so an increase applies immediately (the log says `settings applied: workers=64
lanes=3`) and a decrease as the running extractions finish. The field's description states both.

**D19 closed: the 1x1 px checkbox is the hidden native input, not the control.** jellyfin-web's own stylesheet
makes it so on purpose (`emby-checkbox{appearance:none;height:1px;opacity:0;position:absolute;width:1px}`); what
the user sees and clicks is `.emby-checkbox-label`, measured at 620 x 37,6 px with `cursor: pointer` and a hit
test at its centre returning the label. The framerate-correction switch above it measures identically, so there
was never a scaling defect in the golden-section control. The measurement ran in Chromium with the plugin's own
markup plus that stylesheet (`tests/gui/p4-d19-probe.js`).

Verification: `python3 tests/run_checks.py` passes (781 checks, 0 failures) with 16 new ones - 10 unit checks
(the encoding stored as chosen, a typo replaced *and* reported, the engine's flag set, `--gss` absent without
framerate correction and present with it, the inert tick reported and kept, the lane width) and 6 for the
page/script (the dropdown offers exactly the server's set, the golden-section gating runs on load and on change,
the worker wording, and the save reaching the running scheduler). Integration against a real Jellyfin with the
plugin installed: `tests/backend/p4_settings_probe.py` (11 assertions, all passing - retention, the two notes,
and the `settings applied:` line in the plugin log) and the D19 measurement above.

## 2.0.46 (beta)

One measurement and one set of bounds, both in the family of "what does a long run hold".

**B13 measured at remux scale, and closed: the row's figure was a code bound, not a measurement.** The
register claimed "4 MB window + up to 384 MB of prefetched ranges; with a worker pool that is gigabytes of
prefetch on a batch, an out-of-memory kill in the middle of a run", and nothing had ever measured it at the
scale it is about (the triage's fixture was a 300-cluster file). `tests/backend/b13_probe.py` now does:
30 GB sparse remux fixtures (6 000 clusters, two subtitle tracks, 4 000 cues, blocks sitting late in their
cluster behind a dozen frames), the real extractor, and `GC.GetTotalMemory` sampled every 10 ms while the
passes run - at 1 lane and 8, on local disk and under the rig's slow-storage shim (10 ms per read).

    route                    lanes  peak managed heap   per lane   wall
    cue-indexed (index)          1        12,4 MB        12,0 MB   0,4 s
    cue-indexed (index)          8        86,8 MB        10,8 MB   1,3 s
    cue-indexed, 10 ms/read      8        79,6 MB         9,9 MB   21,7 s
    cluster walk (no index)      1        10,3 MB        10,2 MB  13,7 s
    cluster walk (no index)      8        59,4 MB         7,4 MB   ~14 s
    shared multi-track pass      8        86,9 MB        10,9 MB   1,9 s

So a remux-scale pass holds ~10-12 MB of managed heap, eight at once hold 59-87 MB (the register's claim was
384 MB *per reader*), and the bytes a reader keeps are exactly the ones its own plan priced (3 999 ranges /
4 735 744 B against a 4 736 000 B plan) inside the policy's existing 64 MiB merged-fetch ceiling. Slow
storage changes the wall clock, not the memory, and after the passes the heap is back at its 0,1 MB baseline -
nothing is retained between passes. **No cap was added**, because there is no measured improvement to buy:
the ceiling that bounds the fetch is `ReadPolicy.MaxPrefetchBytes`, and the worst case derived from it is
~84 MB per lane (64 MiB fetch + 4 MB window + 16 MB walk chunk).

**B12: the four stores that outlive a job are bounded, evicted and swept.** Three of the four were real.
`SubtitleCache.Memory` held the *text* of every subtitle the server had ever extracted - `Prune()` pruned the
disk layer only and `Clear()` emptied it wholesale - so a long-lived server accumulated hundreds of megabytes
for a hit the disk layer answers in a millisecond; it is now bounded on both axes (512 entries, 16 M
characters ~ 32 MB of text) with least-recently-used eviction, its size is in the settings summary, and an
evicted entry is still served from disk. `ReferenceStore.Entries` and `SharedExtractionStore.Consumers` were
removed only by the job that created them, so a job that ended without reaching that call (a task that never
unwound after a kill or a stall stop, a job context evicted while its reference was reserved) left an entry
*and a directory on disk* until the next plugin start: the reference store now has `SweepOrphans(isLive)` and
a 512-entry backstop, the shared store's existing `Cleanup` is called during a run, and both are swept by
`SweepLongLivedStores()` from the scheduler's pass *and* the 30-minute cleanup timer (throttled to a directory
walk every five minutes, liveness read from the job table so a reference is never removed from under a running
ffsubsync). `SweepState` was bounded only when its file was read back, so a sweep of a library larger than
5 000 files grew the in-memory dictionary past its own bound; it now trims the least recently touched records
as they arrive.

Verification: `python3 tests/run_checks.py` passes (765 checks, 0 failures) with 20 new ones - 7 from the
memory probe (the ceilings above, asserted through it, and that no lane fetches more than its plan prices) and
10 for the stores (the entry bound with the newest entry surviving, the character bound under 4 x 6 MB
subtitles, the disk prune dropping the memory entry whose file went, an orphaned reference entry and its
directory removed while a live one is kept, an orphaned shared directory removed and its consumer count
cleared, the sweep state inside its bound while 5 060 records arrive), plus 3 source checks pinning the sweep's
call sites.

## 2.0.45 (beta)

Two fixes, for the two ways a batch goes wrong without saying so: a subtitle that was only partly read, and a
job that never finishes.

**A partial extraction is discarded instead of being synced.** The ffmpeg fallback accepted a *failed* run
whenever a file existed at the output path (`exitCode != 0 && !File.Exists(outputPath)`), so a kill, a decode
error or a disk that filled up left a **prefix** of the subtitle behind and the engine was handed it as if it
were the whole track. Reproduced cold with the real ffmpeg: reading a container cut short (the fixture
truncated to 57 %) exits **0**, writes a well-formed SRT holding **17 of its 30 cues**, and reports the
truncation only on stderr ("File ended prematurely") - exit code, file structure and size all look healthy, and
the SRT ends at a cue boundary like any complete one. Every extraction is now judged on four things before
anything downstream can see it: the exit code, ffmpeg's own statement about the input, the file existing, and
the file being a structurally complete SRT. A refusal fails the job with the measured reason ("ffmpeg read a
partial file - it reported 'ended prematurely' - so the 17 cue(s) it produced are only part of the track") and
deletes the partial; an extraction that was stopped (kill, cancellation, extraction timeout) deletes what it
wrote before it unwinds, so a partial can never sit in the shared extraction directory for a later job to
find. A damaged *video* frame ("error while decoding", "corrupt decoded frame") is deliberately not a refusal:
it cannot lose subtitle cues, and failing a job over it hands the user a problem they cannot act on.

**A job can no longer stay `Running`.** A job left in Running holds its worker slot and keeps its batch
unfinished, with a restart as the only way out. Two mechanisms, both closed:

* every exit from a job now settles it - including the one that threw before the job's own error handling was
  reached (a media file that had vanished between queueing and running), which ended the task and left the
  status at Running for good;
* a watchdog stops jobs that stop making progress. It separates waiting from wedged because it knows whether a
  process is running for the job, and when that process last said anything: a job with **no** process and no
  activity for 15 minutes is stopped, while a job with a live process is judged by that process's own output
  over 60 minutes. A demux of a large episode over a share, or a feature film's audio analysis (measured: 5
  minutes of engine silence inside a 6,7-minute run on this server, and a reported 91-minute analysis), is real
  work with quiet stretches - the engine's stderr carried a line every ~0,5 s while it worked, so silence is
  the signal and a live process is never judged by the job's own clock. A job waiting for another subtitle of
  the same file (the file's audio is analysed once and the rest wait on its gate, which can take an hour) is
  spared for as long as that job is working. A stopped job is failed with the reason and the numbers, its token
  is cancelled so whatever it was waiting in unwinds and its slot comes back, and the cancellation handler no
  longer relabels a stopped job "Cancelled by user". The audio-analysis gate wait is cancellable now, so the
  Kill control reaches a job parked there too (it did not, before).

Both windows are settings - `StuckJobTimeoutMinutes` (default 15) and `WedgedProcessTimeoutMinutes` (default
60) in the plugin's config.xml, clamped server-side (2-240 and 5-1440) with the adjustment named on save - and
the plugin log carries the measured numbers when one fires.

Verification: `python3 tests/run_checks.py` passes (744 checks, 0 failures) with 48 new ones - the guard's
decisions on real files (a killed prefix, a partial that ends inside a cue at exit 0, an empty file, a missing
file, a malformed cue, and a damaged-video-frame log that must *not* refuse), the real-ffmpeg integration
through the plugin's own extraction path (a truncated container is refused and nothing is left at the output
path; the same file whole extracts all 30 cues and the file is kept for the engine), and the watchdog's rules,
its selection out of a mixed batch, the stop it applies, the terminal-state guarantee and the process registry
behind it. ffmpeg is not a dependency of the suite: without it the two ffmpeg-backed checks print as SKIP and
the rest still run.

## 2.0.44 (beta)

One fix, on the layer that reads the file, and two counters that make it visible.

**A located subtitle block is read where the index says it is, instead of being walked to.** The Matroska
index usually records both the cluster a subtitle block sits in and the block's offset inside it, and the
plugin fetches exactly that block. On a live pass (`Sunes Sommar 1993 WEB-DL 1080p.mkv`) the plan was
right - the index located all 663 cue points and the plan priced 1,57 MB over 1 319 reads - and the pass
still read **133,45 MB over 33 516 reads, 85,09x its planned bytes and 25,41x its planned reads**:
~50 block headers and ~200 KB of container per block the index had located exactly.

The cause, reproduced cold on a fixture (`tests/backend/s26_probe.py`): a cluster whose size the file does
not state - the EBML unknown-size marker a streamed or partially remuxed file carries - was refused by the
located read, so every cue point fell back to walking its own cluster, once per cue point. Clusters whose
size is stated were never affected, which is why the fixture family this project tests with never caught
it. The located read now bounds such a cluster (by the next cluster the index mentions, or the end of the
file) and, because that bound is not the cluster's own end, keeps the block only when it is the cue
point's own - its time has to agree with the cue point's, so a stale index cannot hand a subtitle another
subtitle's text. The pass also makes the located attempt once instead of twice, and a cluster it walks is
walked once for every cue point that names it, not once each.

Measured on the repro, same file with and without the unknown-size marker, byte-identical subtitles out:

* before: 4 169 733 bytes / 2 007 reads = **44,02x** the planned bytes, **25,09x** the planned reads
* after: 114 765 bytes / 87 reads = **1,21x** / **1,09x**, 2,2 reads per cue point instead of 50,2

Both read-ledger rules (bytes read after the pass had already fetched them, bytes fetched over ranges it
had already read) are 0 B on the repro and on every fixture.

**The log says when a pass falls back to walking.** The extraction line now carries `indexedMisses=` (cue
points whose index entry could not be used) and `walked=` (clusters actually walked). A pass that walks
because of a damaged or foreign index now says so, instead of looking like a plan that held.

**Two log and text corrections.** The extraction note in the dashboard called a subtitle-cache hit "read
through the container index", which is what an indexed read does and not what happened; and the phase text
had no way to name `subtitle-cache` at all. Both now say what actually happened.

Verification: the check suite passes (696 checks, 0 failures) including four new ones that fail without
this fix - the cost invariant on the repro, the counters, the byte-identical output and the ledger - and
the rig's storage scenarios (`s41-steady` at fabji's measured 10 ms/read, `s39-ratio` across a fast and a
slow volume) pass on this build.

## 2.0.43 (beta)

Three things: a setting that lets the offset change across a file that is not one continuous cut, a sweep
that no longer rewrites its state file once per subtitle, and the ruler's shape score in the log.

**Piecewise alignment (`Split penalty`, off by default).** Files are not always one cut: commercial
breaks, inserted or removed scenes, two discs joined into one file. Run with the default 0, the engine
emits a single offset for the whole file - what this plugin has always done - and a file with a break in
the middle is right on one side of it and wrong on the other. Setting the split penalty lets the offset
change across the timeline, charged per split in seconds of overlap (4-20 is typical; lower splits more
eagerly). Measured on the fixture whose second half is 20 s out: the single-offset answer leaves that half
20,0 s wrong, and with the penalty set both halves land within 60 ms.

The plugin's own measurement of the result had to learn the same thing, or it would refuse its own
correct answer: a piecewise result fits no single line, and the guard that refuses a rescale nobody asked
for read it as one (1,01082x on that fixture). A result is now accepted when it is piecewise-*flat* -
every piece internally consistent, no growing drift inside a piece - and refused exactly as before when it
is a ramp wearing pieces. The setting is on the plugin's settings page.

**The sweep's state file is written in batches.** `SweepState` held one JSON document with a record per
subtitle and rewrote the whole thing on every record, so a sweep of a few thousand subtitles wrote a few
thousand times - and the cost grows with the file, because each write serialises everything in it.
Measured in the suite, 2 000 records: **80 writes / 173 ms / 68 MB written**, against **2 000 writes /
4 466 ms / 1 735 MB** before. It is flushed when the queue drains, at the end of a sweep, and at shutdown,
and the check suite holds it to that (100 records must cost 4 writes, not 100).

**The ruler's shape score is in the log (S31 follow-up).** Before a suspicious subtitle ruler is
cross-checked against the film's own audio, the plugin now prints how well the two subtitle tracks agree -
the shape of their alignment curve, after `oseiskar/autosubsync`'s quality-of-fit metrics (MIT). It is
**context only and never decides anything**, and that is the result of building it as a gate first: a ruler
that is the same cut as the film but offset from it correlates with the target perfectly and scores
*exactly* like a correct one (**0,833 at both +20 s, which is right, and +25 s, which is wrong**), so
skipping the cross-check on a high score writes a wrong subtitle in precisely the case the cross-check
exists for - verified end to end on the rig, where the gated build wrote a 25 s-wrong sidecar where this
one refuses and writes nothing. Field evidence agreed the trade was not worth it: across 710 runs that
used a subtitle ruler on a real server, 8 (1,1 %) demanded a shift worth checking at all.

**Verified.** The check suite is green (`python3 tests/run_checks.py`), build 0 warnings / 0 errors, and
the S31 rig scenario passes on all three ruler shapes (correct, different cut, and the same-cut offset that
sank the gate). Details and the measurements: `docs/EVIDENCE_s31_shape_gate.md`,
`docs/EVIDENCE_s31_quality_of_fit.md`, `knowledge/FIX_PLAN.md` rows `s31-quality` and `C2`.

## 2.0.42 (beta)

S40's enqueue cost is now instrumented, and two things that were done while holding the queue lock are not any more.

**Where this comes from.** A field log of a 55-task batch showed every second queued item reporting `log=8 000-21 000
ms` in the plugin's own `enqueue slow:` line. That phase was four different things at once - two dictionary writes,
one line to the plugin log, the queue lock, and waking the pump (which starts the extraction lanes and takes the
same lock twice more) - so the aggregate could not say what the time was.

**What changed.**

- The `enqueue slow:` line now carries the breakdown (`state=`, `logWrite=`, `queueLock=`, `wakePump=`), and
  `SUBSYNC_ENQUEUE_TRACE_MS` lowers the threshold at which it is written (250 ms in the field, so a server that is
  not slow says nothing).
- Every critical section on the queue lock reports its own hold time when it is long enough to be somebody else's
  wait (`queue lock slow: holder=pump-snapshot|pump-plan-outside-lock|cancel-batch|cancel-all ms=…`), so a field
  log names the holder instead of leaving "the enqueue is slow" as the finding.
- The pump plans **outside** the lock: it snapshots the run order under a short lock and runs the plan after it.
  Measured, the planning pass takes ~55 ms, and every enqueue landing in that window used to wait for all of it.
- Cancelling no longer writes a log line per job while holding that lock - one to Jellyfin's sink and one to the
  plugin log, per cancelled job, inside the critical section the enqueue waits on.

**What this release does not claim.** No local speed-up: on the rig the enqueue's `queueLock` figure is identical
before and after (190 ms worst on a box running two 56-task batches, a cancel and an extraction), because that
figure is the enqueueing thread being scheduled out, not contention - and the plugin-log write costs nothing
(`logWrite=0` on every one of 280-392 measured lines, which retires the first guess about this row). The `55 ms`
critical section and the per-job log writes under the lock are gone, and a check brace-matches the lock block out
of `PumpAsync` and fails if the plan moves back inside it. Which holder takes seconds on a real share is the
question a field run answers, and everything needed for it is in this build.

Suite: 665 checks green.

Rollback: 2.0.41's zip remains downloadable at its URL.

## 2.0.41 (beta)

A setting that cannot mean anything is no longer stored as typed, and the engine can no longer be given one.

**Why.** The settings page accepted a negative or absurd offset ceiling, a typo'd output encoding, a language tag
that matches no language, and a binary path that does not exist - and answered "Saved." for every one of them. The
audit drove ten such values through the page: seven were stored exactly as typed. The clamps that did happen
(99 workers became 64) were the page's own arithmetic rather than the server's, so the same values sent to the API
were stored as typed - which the new rig scenario shows. Separately, `--max-offset-seconds` and
`--max-subtitle-seconds` went into ffsubsync's command line from the stored file without ever being re-checked, so a
hand-edited `config.xml` could put `-5` in front of the engine and turn the plugin's own "the engine clamped it"
warning into a constant banner, hiding real clamping.

**What changed.** One validation path, `Configuration/SettingsValidation.cs`, used in both directions:

- wherever a configuration is stored - through `Plugin.UpdateConfiguration`, which every surface passes through -
  values are brought into range and the response says what was adjusted; `GET /SubSync/Settings/ValidationNotes`
  reports those adjustments and the settings page shows them after "Saved." (nobody has to guess what was stored);
- argv and the plugin's own heuristics read the validated values only (`MaxOffsetSecondsOf`,
  `MaxSubtitleSecondsOf`, `MaxSubtitleReferenceOffsetSecondsOf`), so the engine cannot be handed an out-of-range
  number whatever the stored file says;
- a language tag that names no language is dropped, and the note says so - including when that leaves the filter
  empty, in which case every language is synced (a change worth being told about);
- a configured `FfmpegPath` or `FfSubSyncPath` that does not exist is no longer used or handed to the engine.

**Verified.**

    python3 tests/rig/run_scenario.py --scenario d3-settings

| build | hostile values stored as typed | response said anything | result |
|---|---|---|---|
| 2.0.40 | 11 of 11 | nothing | FAILED 11 of 12 |
| 2.0.41 | none | one note per value | PASSED 12 of 12 |

Sample of what the run prints for 2.0.41: `MaxOffsetSeconds=100000 stored as 600 | Max offset seconds: 100000 is
outside 1-600; 600 is stored`; `OutputEncoding='not-an-encoding' stored as 'utf-8' | ... is not one of ascii,
latin-1, utf-16, utf-8, utf-8-sig`; `SyncLanguages=['qq','zz','!!!@#'] stored as [] | dropped ... with none left,
every language is synced`.

Note honestly: this release changes what a save *does* with a bad value, not what the engine does with a good one -
the values a normal installation stores are all in range, and the run's last assertion confirms that an unmodified
configuration is stored unchanged and without a single note. Suite: 664 checks green, 20 new (the ten hostile
values, the language rules, the range accessors, the page's bounds against the server's constants).

Rollback: 2.0.40's zip remains downloadable at its URL.

## 2.0.40 (beta)

The audio is now read as audio, everywhere it is used as an independent signal - not just in the cross-check
2.0.39 added.

**Why.** The default VAD (`subs_then_webrtc`) lets ffsubsync take the video's own **embedded subtitle tracks** as
its speech signal, and the reference this plugin hands the engine for "the audio" is the video itself. So on any
file that carries subtitles - in this library, every file checked but one, one episode carrying 50 tracks - "the
audio path" could be a subtitle path, and *which* track is the engine's choice rather than the plugin's, bypassing
the plugin's own reference checks (cue count, signs-track detection, span against the file). The engine says so
itself: with `subs_then_webrtc` it logs `extracting speech segments from subtitles` instead of extracting speech
from the reference audio. Measured on the fixture used for 2.0.39's cross-check: the same job shape returned the
wrong track's own +24,170 s with the default VAD and the film's -5,080 s once the audio VAD was forced.

**What changed.** The plugin decides, the engine does not. Every run where the plugin intends the audio to be the
reference - the ordinary no-usable-sibling run, the over-ceiling fallback, the wide-window retries and the
verification run, as well as 2.0.39's cross-check - is now given `--vad webrtc`, and the run states in the log that
it overrode the configuration and why. Where the plugin supplied a subtitle reference it has already vetted, the
configured method stands. The speech-cache key now names the VAD the engine is actually given rather than the
configured one it is not given, so two different analyses can no longer share a key.

**Verified.**

    python3 tests/rig/run_scenario.py --scenario s43-audio-is-audio

| build | what the log says about the signal | the engine's answer | result |
|---|---|---|---|
| 2.0.39 | nothing | -30,08 s | FAILED 1 of 3 |
| 2.0.40 | the override and the reason | -30,08 s | PASSED 3 of 3 |

The fixture is a file whose only subtitle track is 30 s out of sync, so the audio is the only usable reference.
Suite: 645 checks green, four of them new (the rule, its reach into every call site, the speech key, the log line).
Honestly: on this fixture 2.0.39 also answered correctly - the engine did not take the subtitle route there - so
what this release adds there is the guarantee and the log line; the divergence it removes is the one measured above
on the wrong-cut fixture, shipped for the cross-check in 2.0.39.

Rollback: 2.0.39's zip remains downloadable at its URL.

# Changelog

All notable changes to this plugin are documented here. Versions follow
`MAJOR.MINOR.PATCH`; the plugin version is also what Jellyfin shows in the plugin list
(release zips are named `Jellyfin.Plugin.SubSync_<version>.0.zip`).

## 2.0.39 (beta)

A subtitle is no longer synced against a reference track that the film's own audio contradicts.

**Why (the Alex class).** A sibling subtitle can pass every check the plugin had - cue count, span against the
file, a demanded shift under the 30 s ceiling - and still not be this film's timeline. Reproduced on the rig with
a real 50-minute episode whose sibling track was the same episode from a 2 % longer cut:

    reference: method=subtitle cues=803 track=s:1
    note: aligned to the reference subtitle s:1 at 24170 ms - check the result; a shift this size usually means
          that track is not the same cut
    job ... completed: output=.../S31 Episode (2026).SYNCED.eng.srt change=+24170 ms

The plugin said the track looked wrong and wrote the file anyway, reporting Completed. The right answer for that
fixture is -5,08 s, measured independently against the film's own audio; the ruler asked for +24,17 s, so the
output was ~29 s wrong with nothing in the job record to show it.

**What changed.**

- **The engine's own alignment score is captured and logged**, for both reference paths. It was printed on every
  run and thrown away: `[job] ffsubsync alignment: score=66451 offset=24.170 s against reference subtitle s:1`,
  and it says when the engine itself calls a sync unsuccessful (a negative score).
- **A suspicious subtitle ruler is cross-checked against the film's own audio before anything is written.** When
  the demanded shift crosses a third of `MaxSubtitleReferenceOffsetSeconds` (10 s at the default), the same
  subtitle is aligned against the film's audio as well and the two answers are compared. Disagree by more than a
  tenth of that ceiling (3 s) and that track is not this film's timeline: it is discarded as a ruler and the
  audio's answer is written. Agree, and the reference's answer stands - a correct 20 s shift is *confirmed*, not
  refused. Both fractions derive from the setting the user controls, so the default behaviour is the one the log
  already described.
- **A ruler whose cues did not move together is refused outright** - the same track from a 2 % longer cut measured
  an interquartile spread of 27,76 s where the file's real sibling measured 0,00 s.
- **The cross-check forces `--vad webrtc` for its one run.** With the default `subs_then_webrtc` the engine takes
  the video's *embedded subtitles* as its speech signal, so the "audio" ruler can be the very track under
  suspicion - measured: it returned the ruler's own answer, the same score to three decimals. That the general
  audio path has the same trap is filed as a finding and is the next item of work.

**Verified on the rig, both directions,** one command each:

    python3 tests/rig/run_scenario.py --scenario s31-wrong-ruler --s31-ruler other-cut    # 5 of 5 assertions
    python3 tests/rig/run_scenario.py --scenario s31-wrong-ruler --s31-ruler correct     # 5 of 5 assertions

    other-cut: the reference subtitle s:1 and the film's own audio disagree (24170 ms against -5080 ms, over the
               3 s they are allowed to differ) - that track is not this film's timeline, so it is discarded as a
               ruler and the audio's answer is written
    correct:   the reference subtitle's shift (-20000 ms) is confirmed by the film's own audio (-20080 ms, within
               3 s) - keeping the reference's answer

The wrong ruler's answer is not written: with the ruler discarded the file has no second track to verify against,
so the job reports `UNVERIFIED: the audio was the only ruler (-5080 ms offset) ... nothing written, source
untouched`. The audio's -5080 ms is the fixture's truth, measured independently (the film's own audio puts the
extracted track at -0,08 s and the target is that track +5 s).

**Checks:** 642 in the suite, green before this commit; thirteen of them new.

**Rollback:** if this misbehaves, the previous build is still served -
`https://makeitmakesencethen.github.io/jellysubsync/beta/Jellyfin.Plugin.SubSync_2.0.38.0.zip`

## 2.0.38 (beta)

A volume's first measurement is a median of three reads, and one read can no longer call a volume thrashing.

**What the field showed (S41, 2026-09-14).** The probe added in 2.0.36 took a *single* cold read, a third of
the way into a file, and whatever it returned became the volume's class for the whole process:

    18:04:21  volume 192.168.0.110:/volume1/JELLYFIN had nothing measured about it, so it was read once:
              16 KB took 231,34 ms - the ceiling for that volume is 1 (this volume measured 231,3 ms per
              read, which is thrashing)

231 ms is over the absolute thrash threshold, so the share ran **one** walk at a time for the rest of the run -
fifteen hold lines over fourteen minutes, all quoting that one read - while its own later walks moved
16,8-23,1 MB/s, which is two walks' worth, and its steady state measured at rest is 13-46 ms. One read is not a
steady state, and the volume was held to a third of its capacity because of it.

**What changed.**

- The first measurement of a volume is now **three 16 KB reads at separated offsets** instead of one read at a
  fixed position, and every one of them is fed to the volume's profile - so the figure the ceiling is decided
  from is a *median* of three, and one slow read no longer becomes the class.
- The log line says what the median stands on: `… so it was read 3 time(s) of 16 KB: median of 3 reads took
  13,11 ms (slowest 81,14 ms) - the ceiling for that volume is 2 (…)`. A verdict that rests on one read is
  visible as such in the field, which is what it was not before.
- **A single read can no longer reach the thrash tier** (`ThrashTierMinReads = 2`). Below that backing, a
  volume that would be called thrashing is held at two walks and the reason says why: *"but that is one read
  and one read is not a steady state, so it is held at 2 until the volume has been read again"*.

**Reproduced, then fixed, on the rig** (local Jellyfin + `slowread.so`, the shim charging 13 ms per read with
the first read on a freshly opened handle charged 231 ms - the field's shape, by construction):

    python3 tests/rig/run_scenario.py --scenario s41-cold-read --timeout 240

- **Before** (2.0.37): `so it was read once: 16 KB took 231,13 ms - the ceiling for that volume is 1 (… which is
  thrashing)` - scenario FAILED, and the shimmed volume got one walk where its own steady state says two.
- **After**: `so it was read 3 time(s) of 16 KB: median of 3 reads took 13,11 ms (slowest 81,14 ms) - the
  ceiling for that volume is 2 (… storage-bound, over 3 read(s))` - scenario PASSED, 6 of 6 assertions, with the
  fast volume in the same run coming out `none`.
- On fabji's measured share (10 ms per read, 11 MB/s) the same scenario reports `ceiling 2` and no ceiling on
  the fast volume: `--scenario s41-steady`.

**Checks:** four new ones (629 total, suite green before this commit) - a verdict from a single read cannot call
a volume thrashing, two reads agreeing can, the thrash verdict names how many reads back it, and the probe takes
more than one read and logs the median.

**Rollback:** if the new probe misbehaves, the previous build is still served and can be installed by URL -
`https://makeitmakesencethen.github.io/jellysubsync/beta/Jellyfin.Plugin.SubSync_2.0.37.0.zip`
(the beta catalogue lists only its newest version, so pinning means that link).

## 2.0.37 (beta)

Fixes the first measurement a volume gets, which 2.0.36 added in the wrong place.

2.0.36 gave a volume with nothing measured about it one timed read, so that a mixed batch could judge a fast
volume instead of guessing at it. The read was taken inside the audio-reference branch of a job - a branch jobs on
a real server do not take. The field run was the proof: seventeen engine runs in that build, several of them on
the fast volume, and **not one probe in the log**. The volume stayed unmeasured, both volumes were held to two
walks each, and a mixed batch ran four jobs at a time with six workers idle.

- The read is now taken from `RunSyncJob`, where every job passes, immediately after the job starts running. That
  is also the right moment: the job is about to read the media, and the next planning pass can then judge the
  volume on the measurement rather than holding it.
- The probe logs what it measured and the ceiling it produced, so the decision can be read out of the log rather
  than inferred.
- A check pins the call site **and** asserts it is not back in the old branch, because that is exactly how this
  went wrong once.

## 2.0.36 (beta)

A volume that nothing has measured gets measured rather than guessed at - and one threshold is recalibrated, so
the behaviour before the change to ratios survives it.

**Why, from a real run.** A mixed batch spanning a share and a local disk produced four walks on the local volume
at 69,9-76,6 MB/s, every one of them discarded: `… but it is not being used to judge that volume: 2 job(s) on
another volume were being read at the same time`. So neither volume had a measurement, both were held to two
walks, and the batch read `dispatch: starting 2, running 0, limit 8, queued 55` for minutes with six workers idle.
The contention rule was doing exactly its job - refusing to be fooled by a walk taken while another volume was
being read - but refusing evidence is not the same as having any.

- A volume with nothing measured about it is now **read once**: 16 KB, a third of the way into the file (a header
  is usually in the page cache and says little about the disk underneath it), once per volume per process, taken
  in the job's own thread just before the engine starts - never while the queue lock is held.
- What the read measured is logged together with the ceiling it produced, so the decision can be read in the
  field instead of inferred.
- **Recalibrated:** the thrash tier is now 400x the machine's best read latency rather than 100x. With the read
  reference floored at 0,25 ms, 100x would have meant 25 ms per read - which would hold a share reading at
  13-46 ms to *one* walk at a time, where its own measurements say two is its best. 400x is the old absolute
  100 ms reproduced from a ratio, which is what "portable" has to mean.

## 2.0.35 (beta)

Two changes to the walk ceiling, both about measuring a volume honestly instead of trusting a number that may
belong to something else.

**A walk only judges its own volume.** A walk's throughput is the storage's speed *and* whatever else happened to
be running. On 2026-09-14 the same local NVMe volume walked at 68-91 MB/s with only its own jobs in flight, and at
45-58 MB/s while a slow share was walked alongside it - which straddled the threshold, so the ceiling held the
fast volume to two walks and a mixed batch ran one file at a time for twenty-five minutes. A walk taken while
another volume was being read is now logged and **not** used to judge the volume: it measures the moment, not the
storage.

**The judgement is relative, not absolute.** Every threshold was a figure measured on one server, and a share or
NAS that genuinely delivers more than 50 MB/s - 10 GbE, or a NAS backed by NVMe - was classified "fast" and got
**no ceiling at all**, so eight full-file walks could land on one volume: exactly the thrash the ceiling exists to
prevent, silently, on better hardware than the thresholds came from. Each volume is now compared with the best
**that machine** has measured, requiring eight samples and excluding the volume being judged, so a volume cannot
set its own bar:

- below half the best walk on this machine is storage-bound (two at a time); two thirds or better is no ceiling;
  in between the last decision stands - that gap is deliberate, because a volume whose walks straddle a single
  boundary flips between two and none inside one batch;
- the read latency decides only when there is no walk to go on, and the read reference is floored at 0,25 ms per
  read: no storage answers faster than that, anything quicker is the page cache rather than evidence about a disk;
- with nothing on the machine to compare against, the previous conservative behaviour applies unchanged;
- and every decision states both numbers in the log, so it can be audited in the field:
  `this volume's last walk moved 3.4 MB/s against the best 91.0 MB/s this machine has measured (0.04x), which is
  storage-bound`.

15 new checks cover it, including a second synthetic machine - a fast NAS - so "works on any machine" is tested
rather than assumed.

## 2.0.34 (beta)

The item-scoped API endpoints now check that the calling account can see the item it names. They used to take
the item id on trust: any authenticated account on the server could read any item's subtitle list, or queue a
sync that **writes a subtitle file**, for an item in a library it had no access to - and the ids were
discoverable through the plugin itself, because the job history is server-wide and its records carry the item id
and the output path.

- Per item, not per role. An account that can see the item behaves exactly as before, so the "Sync Subtitles"
  button on a detail page keeps working for a non-admin. Making those endpoints administrator-only was the
  tempting fix and the wrong one: it would have removed the feature rather than guarded it.
- A request naming several items is refused as a whole when any one of them is not visible, so a batch never
  starts half-queued.
- A refusal does not confirm whether the item exists. The item id goes to the plugin log, which is
  administrator-only, together with the folders the item was found under and the libraries the account holds -
  so a refusal can be diagnosed rather than guessed at.
- The decision lives in one place, `Services/ItemAccess.cs`, and fails closed: an account that cannot be
  resolved, an item that cannot be resolved, an account with no libraries and an item with no resolved folders
  all deny. 17 new checks cover it, including one that fails if any of the four endpoints stops asking.

## 2.0.33 (beta)

The walk ceiling's fast/slow boundary is set between the two throughput populations the field has measured,
instead of inside one of them.

2.0.32 introduced that boundary at 20 MB/s, taken from "the share walks a 2,4 GB file in 903 s = 2,6 MB/s".
The 903 s was measured while **eight walks were already running on that volume** - the volume being throttled
by the very ceiling the number was meant to decide. Walked at its own pace, the same share measures
**16,8-23,1 MB/s** over eight episodes, and local NVMe 84,4-137 MB/s. At 20 the ceiling lifted after a
23,1 MB/s walk and dropped after a 17,4 MB/s one, so a wave planned while it read "fast" could put five
concurrent walks on the share - which is exactly what a field run on 2026-09-14 did.

- The boundary is now **50 MB/s**: 2,2x above every walk this share has produced, 1,7x below every local walk.
  The whole share range maps to the same ceiling, so it can no longer flip between planning passes within a
  batch.
- Nothing else changes. The ceiling still measures itself from the volume's own reads and walks, still holds an
  unmeasured volume at 2, and still lifts for a volume that measures fast: in the same field run local storage
  walked at 84,4-91,4 MB/s with three concurrent walks and `ceiling none (fast)`, while the share stayed at
  33-116 s per walk against 5,5-7,0 min before the ceiling existed.

## 2.0.32 (beta)

The walk ceiling can now measure itself, and it says which measurement held a job.

- **A volume can be measured by its own walks.** Until now the profile was fed only by reads the extraction
  path made, and on a warm subtitle cache that path reads nothing - so a volume could keep the conservative
  ceiling of 2 for ever, on fast storage as well. A finished audio-ruler walk now records the media file's
  length and the engine's wall clock against its volume, and the ceiling takes the **more conservative of the
  two measurements**, the read latency and the walk throughput. The thresholds are measured, not chosen: 5 and
  100 ms per read, 20 and 1 MB/s for walks - local disk walks a 2,4 GB file in 17 s (137 MB/s), the same file
  off the share in 903 s (2,6 MB/s), a whole season at 3,1-3,5 MB/s.
- **A volume nothing has measured is still held at 2**, so the hole 2.0.30 shipped stays closed: not known to
  be fast is not the same as fast. A fast volume leaves that case as soon as its own walk has finished, and
  nothing changes for a setup that is not storage-bound.
- **The hold line names the measurement** - in milliseconds per read or MB/s - instead of choosing its wording
  from the ceiling value. Until now "nothing measured yet" and "measured storage-bound" printed identically,
  which is how a working ceiling read as a broken one in a real run.
- Every audio-ruler walk now logs what it measured, so the ceiling can be checked from the log alone:
  `this walk moved 1876,4 MB of <file> in 402,0 s = 4,7 MB/s - the ceiling for that volume is 2 (this
  volume's last walk moved 4,7 MB/s, which is storage-bound)`.
- A walk is never recorded as a read: the two are kept apart, so a whole-file walk cannot pose as the cost of
  one read.

## 2.0.31 (beta)

The per-volume walk ceiling now applies to a volume nothing has read yet - which is exactly the case it is
most needed in.

2.0.30 shipped the ceiling with "no measurement means no ceiling". The measurement is filled in *during* the
extraction pass that reads the file, but the wave of jobs is planned *before* it, so on a cold store the
ceiling was never consulted with anything in it and it did nothing. Found on a real server, on a season sync
that was meant to exercise it:

- **eight audio walks started within twenty seconds on one share** - max concurrent **8**, median **6,8 min**
  per walk (5,5-7,0) - while the extraction pass measured that volume at **7,7-39 ms per read**, far above the
  5 ms that should have held it to two.
- Two holes rather than one: `WalkCapForProfile(null)` returned no ceiling, and `WalkCapOfPath(null)` - a job
  whose volume could not be resolved at plan time - returned none either. Either one admits a whole batch.

Both now treat an unmeasured volume, and a volume that cannot be identified, as **not known to be fast**: each
is held at 2 until that volume's own reads say otherwise. A volume that measures fast - a local disk answers in
~0,05 ms - is uncapped from its first measured pass onwards, so the cost to a setup that is not storage-bound
is at most its first heavy wave at two instead of N.

- **The ceiling is no longer silent.** Holding a walk now logs, once a minute per volume:
  `walk ceiling: holding <volume> at 2 concurrent media read(s) - this volume has not been read yet, so it is
  treated as slow until it measures fast`. The absence of any such line is what made the first run ambiguous.
- **Checks pin the ordering, not just the arithmetic**: eight heavy jobs against a cold store must plan
  **2 of 8**, an unmeasured real volume must be held at 2, and a volume whose own reads measured it fast must
  be uncapped. 559 checks green.

Nothing else changed: the extraction route, the reads it makes, the alignment and the files it writes are
untouched.

## 2.0.30 (beta)

A slow share now bounds its own audio analysis, and the log answers what a long batch is doing while it runs.

The audio analysis of a file - the one pass that reads the whole container - is now bounded **per volume**,
automatically. A batch spanning a fast disk and a slow share no longer runs eight walks at once on the share:
the share's own measured read latency decides, and there is nothing to configure.

- **Measured before it was built.** One season, eight episodes, one share, worker limit 1 / 2 / 4 / 8: per file
  22,2 / 48,9 / 94,1 / **421,3 s**, batch wall clock 3,9 / 3,6 / 3,6 / **7,3 min**, and 122 / 135 / 133 /
  **66 files per hour**. Eight walks at once was not parallelism: it halved the throughput of the volume and
  turned a file that takes 22 s alone into one that takes 421 s.
- **The ceiling comes from the volume's own numbers.** `VolumeProfile` - already built for the read policy -
  tracks each volume's latency and throughput from reads the passes were making anyway. At or above 5 ms per
  read a volume counts as storage-bound and carries a ceiling of 2; at or above 100 ms per read (the share
  thrashing - 1419-1613 ms per read was observed under eight walks) it carries 1. Below that, and for any
  volume nothing has measured, there is **no ceiling at all** and behaviour is exactly as it was.
- **The ceiling counts one volume.** A slow volume's number never touches another volume's jobs, which is what
  makes it safe rather than a global throttle. The check drives the real dispatch entry with two genuinely
  different devices and asserts the storage-bound volume takes 2 of its 3 jobs while the fast one takes all 3.
- **An old invariant was reversed on purpose, and the reason is recorded** in `knowledge/FIX_PLAN.md`. The
  regression checks used to enforce "the worker count is the only bound, never storage", by reflection as well
  as by behaviour. That rule was measured wrong on a storage-bound volume, and the ceiling is per volume
  precisely so it cannot cause the global slowdown the old rule existed to prevent. The checks were rewritten
  as behaviour, not renamed around: 541 -> 557 checks, all green.

Long engine runs are now visible while they run, and three previously silent outcomes leave a trace:

- **A running engine says so every five minutes** - `engine running`, elapsed time and which ruler - so a film
  that legitimately takes an hour no longer looks like a hang.
- **A completion that wrote nothing is traceable in the plugin log**: both no-change paths now emit the same
  completion line as every other job.
- **A cancelled job has a line of its own**, alongside the unchanged batch-level cancel counts.
- **A periodic progress line answers how much is left**: queued, running, files left, and the file a lane is
  working on with the time it has spent on it.
- **Batch history survives a plugin restart**, persisted with the plugin's other state.

A selection larger than the API accepts is now split instead of refused:

- The page splits a selection of more than 1000 tasks into consecutive batches the server accepts. Before, such
  a selection was answered with `Batch is too large (max 1000 tasks)` after the whole run had been queued.

Nothing about a sync's result changes: the extraction route, the reads it makes, the alignment and the file it
writes are untouched, and no new setting was added - the ceiling is derived, never configured.

## 2.0.29 (beta)

The extraction summary's own figures are now the figures the pass finished with.

`FinaliseStats` derives average ms/read and blocks/s from the counters a pass ends with, and both call sites
assigned those counters *after* calling it - so the block rate was 0 on every extraction that read a file. The
ms/read figure came out right only by accident: the cue-indexed loop's live counter line fills its half as it
goes, which is why nothing you read in the log ever showed the zero.

- Reproduced before touching the code, on all four fixture shapes: `blocks=10 totalMs=0.8 perSecond=0.00`.
- Fixed by assigning the counters first, at both the success and the failure call site. The check now asserts the
  figure equals `SubtitleBlocks / (TotalMs / 1000)` exactly rather than merely being non-zero - after the fix,
  15 908 / 8 221 / 14 120 / 10 137 blocks/s on those same fixtures.
- The summary's text recomputed blocks/s itself as a workaround for the zero. That second copy is gone, because
  it produced the same number from the same total time: nothing you read in the log changes.

No change to a sync itself - the extraction route, the reads it makes and the file it writes are untouched.

## 2.0.28 (beta)

The queue now names a queued subtitle the same way the extraction lane counts subtitles.

A job is queued against a subtitle stream, and Jellyfin numbers that stream by its position among *every* stream
in the file - video and audio included. The extraction lane, the subtitle cache and the one-pass reader count
subtitle tracks only, so on any file whose subtitles are not its first streams the two numbers differ by however
many streams sit in front of them. The queue stored Jellyfin's number and handed it to code that wanted the other
one.

- **Five jobs of the last test run were refused for it**, all in the same shape, all on files whose subtitles sit
  behind a video and one or more audio streams: `subtitle ordinal 11 out of range (11 tracks)` (Egghead Republic,
  and the lav track of four Thunder in My Heart episodes). On files where the wrong number happened to land inside
  the range, the extraction lane read a neighbouring track and cached its text under a key no job ever reads - a
  wasted pass, not a wrong subtitle, because every job resolves its own stream with ffmpeg and extracts what it
  needs itself.
- **The translation happens once, at the queue.** Reproduced first on a fixture of the failed file's shape (one
  video stream, one audio stream, eleven subtitle tracks: stream 11 is the tenth subtitle, ordinal 9). Before, the
  queue handed over 11 and the lane refused it with the line above; after, it hands over 9 and the lane extracts
  that track's own text. One definition now serves the queue and the run-time resolver, so the track a job was
  queued for and the track it reads cannot drift apart.
- **The queued log line names both numbers** (`stream=` and `ordinal=`), so reading the log cannot confuse the two
  again.

Nothing changes for files whose subtitles are their first streams: the two numbers are the same there, which is why
24 of the 29 extraction passes in that run were never affected.

## 2.0.27 (beta)

Two extraction defects from the Tier 4 list: a progress line that never counted, and a cue index key that could skip a subtitle.

The metadata scan is the route that reads most of a file - it is what runs when a track's cue points are missing,
or name their clusters but not the blocks inside them - so on a network share it is the slowest thing this plugin
does, and its progress line printed the reading counters that only the cue-indexed route fills. It read "0,0 MB"
from the first cluster to the last.

- **The scan reports what it has read.** The line takes its numbers from the reader the pass is reading through
  (the kernel's counters where the platform provides them, the reader's own count otherwise), and the pass's own
  counters are brought up to date with it, so the summary at the end carries the same figures the live lines
  showed. Measured on a 520-cluster fixture: `scanning clusters (400 read, 0,0 MB, 200 subtitles found)` became
  `scanning clusters (400 read, 1,6 MB, 200 subtitles found)`.

The cue-indexed loop skips a cue point it has already seen, keyed by `cluster position * 31 + block offset` - a
hash of two numbers rather than the pair itself. Two cue points whose clusters are d bytes apart and whose block
offsets differ by exactly 31d therefore share a key, and the second one was skipped without its cluster ever being
read: its line was lost, and the pass reported success.

- **The key is the pair it means.** A foreign or damaged cue index is where such a pair comes from, and this project
  has met two of those already. Reproduced with a generator written for it (`tests/fixtures/make_collision.py`:
  clusters 67 bytes apart, one cue point carrying an offset of 31 x 67 that points outside its own cluster, the next
  sitting at offset 0 where its block is): one cue before, two after, and the same file with the first offset nudged
  by one byte kept both cues either way. Nothing moved on the files this work was measured against: kopps 829 cues,
  Sune i Grekland 1019, the D17 mixed-index track 803.

## 2.0.26 (beta)

A cue index's offsets now belong to the track they name, and a block is one subtitle however many routes reach it.

One cue point carries one CueTrackPositions per track: mkvmerge writes the video's position and every
subtitle track's position into the same cue point, the highest track number last. The parser read the
cluster and the block offset into variables that outlived each CueTrackPositions and adopted whichever came
last, so a cue point that matched the track being extracted still handed it a neighbouring track's offsets.
On one 9,4 GB film 104 of 829 cue points named another track's block; on a 4,9 GB one, all 1019 did. Every
one of those fell back to walking its cluster to find the block, and a walk cannot know which blocks a
neighbouring cue point already emitted - so one subtitle came out twice, once carrying its own duration and
once the two-second default this extractor uses when a block has none, which the writer's overlap pass then
cut to 1,5 s. Two copies of the same line, with different end times.

- **The offsets are attributed to the track they belong to.** The index locates the block it names, so the
  walks stop happening. Measured against the file's own cue index and against ffmpeg: the 9,4 GB film went
  from 832 cues to 829 where the index and ffmpeg both say 829, and the 4,9 GB one from 1251 to 1019 where
  both say 1019. Cluster visits fell from 933 to 829 and from 2038 to 1019.
- **A block is one subtitle however many routes reach it.** Where an index locates only some of a track's
  cue points, the walk over a cluster used to emit a block the cue point beside it had already emitted. A
  pass now remembers the blocks it has emitted, so the mixed-index fixture yields 803 cues instead of 843 -
  and 803 is what ffmpeg reports for it.
- **The cluster count says one visit per cluster.** A walked cluster was counted twice, once by the cue
  point that named it and once by the walk itself, so the log read 15 visits for 10 cue points and 1205 for
  803 cue points. It reads like the reference extraction now.

## 2.0.25 (beta)

The reading decisions now say what they rest on, and a prediction that cannot be verified says so instead.

2.0.24 put every read decision in one place and made each phase print what it expected beside what it cost.
Three things kept that from being trustworthy: a plan whose cost is not knowable before the work was
compared as though it were an estimate and could never warn; the profile's counters were read through names
that did not mean what they said; and the shared multi-track phase was compared against a plan that another
phase had already paid for.

- **A bounded prediction is labelled as one.** The cluster walk stops once it has found the blocks it came
  for, so what it will cost is not knowable from the index, and a bound tight enough to warn would warn
  wrongly. Those plans carry `[bound, not verified]` in the log line, the walk's cost is held to something
  that can fail instead (one window per cluster it visits), and the checks assert the marker is on exactly
  the bounded plans and never on a plan the index fully locates. A prediction that silently passes whatever
  the pass does reads as verification, which is worse than having none.
- **The profile's counters name what they count.** `Measured` read the pending sample window, which returns
  to zero every eight reads, so it was true only in the instant the eighth sample of a round arrived - and
  nothing called it. The log now separates the defaults from a measurement and says how much it rests on:
  `storage not measured yet - deciding from the defaults (0,05 ms per read, 1,5 MB/s); merge gap 0 KB, no
  read observed` against `storage 12,00 ms per read and 0,2 MB/s (8 read(s) over 1 update(s)); merge gap
  2 KB`.
- **The shared multi-track phase is no longer reported as missing a prediction it met.** It is served out of
  the primary track's fetch, so its plan overstates what it will read; compared raw it logged
  `shared-pass expected 0,62 MB/520 read(s), actual 0,00 MB/0 read(s) - this pass missed its own prediction`
  as a warning on every file with two or more subtitle tracks. The line is logged with both numbers and how
  much the phase read of its own, and the fetches it lives on stay accounted for by the phase that made them.

## 2.0.24 (beta)

One policy decides how a pass reads the file, and it decides from what the storage costs while the pass runs.

Extraction chose its read window in three separate places - the cue-indexed loop, the cluster walk and the
shared multi-track pass - from a constant, or from a probe taken before the work started. The probe's own
numbers swing by two orders of magnitude while other jobs load the same disk (0,4 ms and 255 ms per 16 KB on
one box within minutes), and the constants were wrong in both directions: a walk-sized window leaked into
reads whose position the index had named exactly (779 cues, a 4 MB window each, 3,2 GB moved to collect
~50 KB of text), and a 4 KB window made every cue a round trip on a share that charges for one.

- **`ReadPolicy` is the one decision.** A pass is handed a `ReadPlan` - the route, the window for reads the
  plan did not foresee, the reads it expects to make, and what it therefore expects to cost. The cue-indexed
  path, the cluster walk and the shared pass all take their plan from it and follow it; every window set
  afterwards comes from the policy.
- **The storage is measured from the reads the pass is making**: milliseconds per read call and bytes per
  millisecond, both updated as it runs and both logged (`extract profile: ... storage 10,18 ms per read,
  0,4 MB/s measured over the pass's own reads`). A read answered from a fetched range is not billed as a read.
- **The alternatives are priced, not pinned to a ratio**: covering a cluster's blocks in one read against one
  read per block header, and merging two nearby block reads into one when the bytes between them cost less
  than the round trip they save.
- **Every phase states its expected cost and prints it beside the actual**, and a pass that misses its own
  prediction by more than a factor of two logs a warning with the numbers.
- **A ledger inside the pass counts the waste**: ranges fetched that no read used, and reads that went to the
  file for bytes the pass already held. Both are logged, both must stay at zero, and neither could be seen
  before - 2.0.23 fetched 118,3 MB and then read past it without the log saying so.
- **The regression suite grew a matrix** (`python3 tests/run_checks.py`): cue index that locates its blocks /
  one that does not / no cue index at all, x 1 track / 32 tracks, x four storage profiles (fast, 10 ms per
  read, 11 MB/s, both), asserting the route chosen and the cost planned for every cell, all synthetic. On the
  fixtures it asserts the on-disk form of the invariants: no fetch unused, no read past the fetch, never more
  bytes than the file plus one window, and every phase's plan matching what it cost.

Measured with `python3 tests/backend/read_rig.py`, which runs the real extraction over a 0,5 GB episode whose
subtitle blocks sit most of a megabyte into their cluster, and a 1,3 GB remux whose cue index does not locate
its blocks, on a share modelled as 10 ms per read **and** 11 MB/s (the numbers from fabji's own plugin log):

| pass | 2.0.23 | 2.0.24 |
|---|---|---|
| 0,5 GB episode, blocks late | 325,21 MB / 338 reads / 16,51 s | 0,65 MB / 527 reads / 1,54 s |
| 1,3 GB remux, no block offsets | 1086,48 MB / 275 reads / 100,07 s | 1,08 MB / 267 reads / 2,92 s |

- Both passes now read the subtitle, not the file: 500x fewer bytes on the episode (1,54 s against 16,51 s)
  and 1000x fewer on the remux (2,92 s against 100,07 s), on the same storage and the same code path.
- **Three defects this work found in itself, all fixed here.** (1) The prefetch ceiling was only checked for
  ranges that did not merge, so 32 blocks four kilobytes apart merged into one range per cluster and a pass
  that planned to fetch 67,1 MB fetched 119,2 MB. (2) Each phase measured its own cost from *after* its plan
  had been applied - including the prefetch the plan asks for - so a pass that followed its plan exactly
  reported "0,00 MB, 0 read(s)" and was flagged as missing a prediction it had met. (3) The rig's storage
  model charged the per-read latency *instead of* the per-byte cost, so a pass that moved 325 MB looked 30 s
  cheaper than it is on the share it models; it charges both now, which is what the numbers above are
  measured with.
- **What this does not change**: a pass whose plan is a bound rather than an estimate (the cluster walk says
  "about the region" and stops when it has found what it needs) is marked coarse and is not compared for a
  miss, and the cue-indexed route still plans one read per located block, so its read count follows the cue
  count even where the storage charges per read. The bytes are what the fetches and the ledger bound.

## 2.0.23 (beta)

The prefetched ranges now start at the cluster, so the pass finally reads from memory.

The per-cue loop reads a cluster's header first (to find where its children begin) and only then the block
the index named. The prefetch built its ranges from the block position - or from the first block's relative
offset, which is relative to the cluster's *data* start - so that first read fell outside every range and
went to disk. The fetch was made and then ignored: one file fetched 118,3 MB and the loop still issued 2 282
real reads, 70,9 s of which was waiting on the same share that had just answered the prefetch.

- **Both prefetches cover the cluster head now**: the cue-indexed path and the shared multi-track pass.
  The reads the fetch was made for are served from it, so the loop stops issuing one real read per cue.
- **What this does not fix**: a file whose cue index does not locate its subtitle blocks has to walk the
  clusters, and walking reads about the file - on a share delivering ~11 MB/s that is ~2 minutes for a
  1,3 GB remux however the reads are arranged. Locating those blocks from the video track's own index
  instead of walking is the next piece of work; it is measured, not guessed, and is not in this release.

## 2.0.22 (beta)

The cue window now adapts while the pass runs, because a probe taken before the work cannot size it.

2.0.21 sized the cue read window from a probe of the storage, and on the storage it was built for it changed
nothing: the rule refused to grow the window unless a 1 MB read cost about what a 16 KB read costs, and on
the share in question it does not. The log from that machine shows what that leaves - 1500 reads per pass at
6 KB per read, 46-114 s per file, against 0,3-3 s on its faster volumes.

Two more things were wrong with deciding this before the work starts. The probe's own numbers swing by two
orders of magnitude while other jobs and lanes load the same disk (0,4 ms and 255 ms per 16 KB on one box
within minutes), and taking it costs a 1 MB read per file.

- **The pass times its own reads and adapts.** It starts at 4 KB; if the reads it is actually making average
  4 ms or more, the window quadruples (bounded at 1 MB); if they average 0,5 ms or less, it quarters (bounded
  at 4 KB). Every change is logged, so the log shows the pass learning its storage.
- **The pre-work 1 MB probe read is gone**, and with it the ratio gate that made 2.0.21 inert there.
- A pass on cheap storage still reads a small fraction of the file: that guard is unchanged, and it now says
  which of the two regimes it is checking.

## 2.0.21 (beta)

Extraction stops paying for one round trip per subtitle cue.

A cue-indexed file reads one small piece per cue, and that window was hard-coded to 4 KB. On a share that
charges per round trip - the log shows 29,84 ms per 16 KB read - a file with 850 cues spent ~12 s of pure
waiting on reads that carried 5 MB in total, and a pass over one file measured 21 s while the sync work
that followed it took 1,5-2 s.

- **The window is now sized from the storage, both halves of it.** The probe already timed a small read;
  it now also times a 1 MB read, so the window becomes the bytes one round trip can carry (the
  bandwidth-delay product, clamped to 4 KB … 1 MB). A share that charges ~13 ms per round trip carries a
  megabyte in one, so its cues come back a few dozen reads at a time instead of one read each; local
  storage where a read is free stays at 4 KB and reads no more than before.
- **The probe line says what it measured**, so the choice is visible in the log rather than implied:
  ms per 16 KB read, KB per round trip, and both chosen windows.
- **Nothing else about a pass changed**: the multi-track pass keeps its bounded window, the scan window
  keeps its own rule, and the extracted subtitles are the same bytes.

Worth saying plainly, since it looked like a worker shortage: three extraction lanes were already running
in the batch that prompted this (the log shows all three starting), and the syncs between them were 1,5-2 s
each. One job ran at a time because a file's job cannot start until that file's pass finishes - the pass
was the queue.

## 2.0.20 (beta)

The offset ceiling is a search window, so it now has room - and the rigid-shift attempt is gone.

A real file syncs when "Maximum offset" is 150 and not when it is 60, and that is the whole story: ffsubsync's
`--max-offset-seconds` is the range the alignment may look in. With 60 s the engine could not see an answer at ~112 s
and returned the best *wrong* one it could find (56 s, then 60 s). No plugin logic can repair an answer that is
outside the searched range.

- **Default raised to 180 s.** It mirrors upstream's 60 s default, which is simply too small for real libraries.
- **A result that lands on the window is retried once with twice it**, and written only when it is not pinned to the
  wider window either and one alignment against the film's audio asks for nothing more. If the wider window is pinned
  too, the job refuses and says to raise the setting.
- **The rigid-shift path is deleted.** It applied the measured displacement as a pure shift, but when the engine also
  re-times a subtitle (a framerate correction) that number is the median displacement of a rescaled timeline, and
  applying it as a shift is wrong by construction. Tried in 2.0.19; wrong on the file it was written for.
- The setting's description and AGENTS.md now say what the value is: a search range, not a trust limit.

## 2.0.19 (beta)

A subtitle further out than the offset limit is shifted by the measurement the engine already made, and then checked
against the film's audio. The widened search is gone.

2.0.18 enlarged the search window to 300 s to reach a subtitle two minutes out. On a real file that was wrong: with
the wider window the engine locked onto a **different** part of the audio (56 s where the first pass had measured
94 s), so the subtitle it wrote was wrong from the first line - and the check it was paired with re-ran the *same*
configuration, so it agreed with itself and could not object. A wider window invites a wrong lock; that is the lesson
this release is built on.

- The shift the first pass measured is now applied **rigidly** - pure timestamp arithmetic, nothing to lock onto -
  and that shifted subtitle is aligned against the film's audio with the **normal** allowance. A correct shift leaves
  almost nothing, and the engine's own output is the result; a wrong shift leaves a large residual, and the job
  **refuses and writes nothing** instead of handing over a confidently wrong subtitle.
- `--max-offset-seconds` is never enlarged, which also removes the 300 s value that made one of the retries exit 1.
- Order fixed: the subtitle-reference ceiling (the bad-ruler check) now runs **before** the offset work. In the
  user's log the retry ran first and aligned against a ruler already known to be from a different cut.
- A failed alignment run now reports what the engine said (its last lines), not just the exit code.

## 2.0.18 (beta)

A subtitle further out than the offset limit gets one wide retry, checked against the audio again.

"Maximum offset" (60 s by default) exists because a result pinned to it is the most the engine was allowed to apply,
not what the file needed. Refusing is safe and unhelpful: subtitles two minutes out are real, and the job used to end
there with nothing written.

- A result at or past the limit now sends that subtitle through **one more alignment with a wide allowance** - four
  times the configured ceiling, at least 300 s - instead of ending the job.
- The wide result is then **checked again with a tight allowance** against the film's own audio, the same double-check
  a framerate stretch gets. If the film agrees (only seconds left to fix) the result is written; if the wide pass
  locked onto the wrong part of the audio, the tight pass still asks for a large shift, and the job refuses with the
  numbers, writing nothing.
- The message is honest now: a 94 s shift is reported as "at or past the 60 s ceiling it was allowed", not as
  "sitting on" it.
- The hold-predicate behind both checks is one function (`AlignmentHoldsAgainstAudio`), so the stretch check and the
  wide-retry check cannot drift apart.

Verified by `tests/backend/wide_allowance_fixture.py`: the fixture's subtitle is the file's own track 120 s out - past
the 30 s a subtitle reference is trusted for, past the 60 s offset ceiling, inside the 300 s retry - and the run
completes with the written subtitle within seconds of the film's own track, after the log shows the retry and the
second check.

## 2.0.17 (beta)

A subtitle reference that is not the same cut is replaced by the audio, instead of ending the job.

A file's own subtitle track is a free and exact ruler - 265 ms and 8.7 MB to read, against minutes of audio analysis
- so it is used when it exists. When it is from a different cut, the alignment it produces is nonsense: on the file
this came from, the embedded track demanded a 111.9 s shift of the user's subtitle. That is what the
`MaxSubtitleReferenceOffsetSeconds` ceiling (30 s by default) exists to catch, and until now it refused the subtitle.

- The ceiling still governs what a subtitle reference is trusted for. Past it, the track is now **discarded as a
  ruler** - so the file's other subtitles do not repeat the same measurement - and the subtitle is aligned against
  the **audio** instead, which cannot be a wrong cut. The result is written.
- Cost lands only on files with a bad reference: one audio analysis per file, cached like every other audio path, and
  a log line saying so. Files whose reference is fine are untouched.
- The outcome says it plainly: "the file's own subtitle track is not the same cut, so this was aligned against the
  audio".
- If the audio alignment after a bad reference produces nothing, the job still refuses and writes nothing.
- **Contract change**: `MaxSubtitleReferenceOffsetSeconds` was documented as a refusal; it is now the trigger for the
  audio alignment. AGENTS.md is updated with it.

Verified with `tests/backend/bad_reference_fallback.py`: the fixture's subtitle is the file's own track 45 s out -
past the 30 s a subtitle reference is trusted for and inside the audio path's 60 s - and the run completes with the
written subtitle 0.0 s from the film's own track, where before it refused.

## 2.0.16 (beta)

A stretch is tested against the film's own audio, and only when something was stretched.

The spans cannot tell a subtitle timed for another framerate from one taken from a different cut - both show the
same few percent - so a stretch could be applied to the wrong kind of difference, leaving a subtitle that reads
correctly for the first minutes and is minutes out by the end (measured: a 4.17% span difference became a 0.960x
scale with -104 s of drift). The film's audio can tell them apart.

- After the plugin rescaled a subtitle, ffsubsync is pointed at the media file (its audio, default stream) with the
  **stretched** subtitle as input and rescaling forbidden, so what comes back is the residual rather than a second
  opinion about the ratio. If little is left (10 s, or 0.5% of the runtime) the stretch holds and the audio-aligned
  result is written; if a large shift or a second rescale is asked for, the stretch is dropped, the subtitle you have
  is aligned against the audio with offsets only, and both the log and the outcome say the stretch was dropped.
- It runs **only when a stretch happened**, so an ordinary offset-only sync is untouched, and only the first
  stretched subtitle of a file pays for the audio analysis, which is cached per file like every other audio path.
- Observed on the fixture: `the subtitle was stretched, so the stretch is tested against the film's own audio` then
  `the audio confirms the stretch (a further 0 ms, no rescale)`, about two seconds.

The limit, stated plainly: this catches a differently cut subtitle whose error is not a uniform scale (an extra
scene, longer credits, a re-edit), which is the usual shape. A cut that differs by exactly a uniform ~4% is
numerically the same problem as a PAL mismatch and no cheap measurement separates the two.

## 2.0.15 (beta)

Framerate correction works on every reference path, and is on by default.

- **On by default.** A subtitle timed for a different framerate (PAL 25 against a 23.976 fps release) is
  stretched onto the video's timeline without having to be asked for. The setting stays, so it can be turned off
  for the case it cannot distinguish: a subtitle from a longer cut looks numerically identical, and stretching
  that one leaves the end of the film minutes out of step (measured: a span 4.17% too long became a 0.960x scale,
  a -51.9 s shift and -104 s of drift). Either way nothing is stretched silently - a result that is not a real
  framerate pair is refused with the numbers.
- **It now works when the alignment reference is another subtitle**, which is where it did nothing before. A
  subtitle reference has no frame rate for the engine to read, so the engine could only fit a shift: a PAL-timed
  subtitle came out as a pure shift of about half the film's drift - on a user's file 111.9 s over 96 minutes -
  and the 30 s reference ceiling refused it, so sync failed on exactly the file the option was turned on for. The
  plugin now measures both spans itself, rescales the subtitle onto the reference's time base when the two are a
  framerate pair apart, and leaves the aligner the small shift it is good at. A span difference that is not a
  pair is a different cut: nothing is rescaled, and the alignment reports it as before.
- **The engine's span-based ratio inference is kept out of the subtitle-reference path.** It has no frame rate
  to read there and compares the reference's duration with the subtitle's span instead: measured on a 50-minute
  fixture whose subtitle was one PAL step off the reference, enabling framerate correction this way turned a
  17.4 s shift into a 55.8 s one, and the reference ceiling then refused the file. The plugin owns that decision
  on this path - it sees both spans and only accepts a real framerate pair - so the inference stays out of it.
- **The rescale is anchored to the file's own duration, not to whatever the reference is.** Both a subtitle from
  a different framerate and a mis-timed *reference* look like the same few percent, so the references alone cannot
  say which side is wrong. The file's duration can: a subtitle spans the film it was timed for, so the side that
  disagrees with the duration is the one to correct. This matters most in bulk, where every subtitle of a file is
  aligned against the same reference - a reference taken from a PAL release would otherwise have stretched every
  correct subtitle of that file onto it, and the old reference ceiling that used to refuse such a file no longer
  fires once the shift is small.
- **A rescaled result is reported as a rescale.** The outcome used to compare the subtitle you had with the
  corrected file, which are on differently scaled timelines: a correction read as "change=+55388 ms" on a
  50-minute file, i.e. as if the alignment had gone badly wrong. It now says the factor it stretched to and the
  alignment's own change.
- The label no longer says "(advanced)", since this is now the default rather than a specialty.

Verified with a fixture (`tests/backend/reference_framerate.py`, run by `tests/backend/run_reference_fixture.sh`)
whose subtitle is one PAL step off the reference the plugin itself uses: with the option on, the log records the
rescale, the job completes, and the written subtitle's span matches the reference's (median offset 0.0 s); with it
off, no rescale is attempted and the written file keeps its 0.95904 span. The other direction - a correct subtitle
against a mis-timed reference - is covered by unit checks in the generated C# harness, because the plugin writes
its reference per run and removes it with the run, so no fixture can make that reference PAL-timed.

## 2.0.14 (beta)

Wording only - no behaviour change.

- **The framerate option now says the trade-off instead of the engine's internals.** It was: "the engine infers a
  framerate ratio from the subtitle's own span, so a release with a slightly longer runtime gets time-scaled".
  It is: it stretches the timings to fix a mismatch (a subtitle timed for PAL 25 fps on a 23.976 fps file), leave
  it off when the subtitle is simply from a different cut because that looks identical to the engine and
  stretching it leaves the end of the film minutes out of step, and either way nothing is stretched silently - a
  result that is not a real framerate pair is refused with the numbers.
- **No flag names in the interface.** "Enable --gss to find the optimal framerate ratio" is now "Searches harder
  for the exact stretch factor. Slower, and only useful together with the option above."
- **Speech detection says what it decides** ("how the engine decides which parts of the audio are speech")
  rather than which component does it.

## 2.0.13 (beta)

The run indicator, and a queue that says what it is waiting for.

- **A queued run says why it is waiting.** "waiting to start" was not an answer - on network storage the wait
  is the file's subtitle extraction (46-96 s before the first job of a file can start), and the planner knows
  both reasons. The interface now shows the reason the job itself carries:
  * `Reading subtitles from the video - this one starts as soon as its track is out` while the file is being
    read;
  * `Waiting for a free worker (3 of 4 busy)` when the subtitle is ready and the slots are full;
  * `Queued - starting as soon as a worker is free` when the server has not said more.
- **The page shows that it is working**, not just that it is waiting: the same small spinner the sync dialog
  in the item menu uses (12 px ring, .8 s) runs next to the run line while a run is queued or working, and
  stops when the run finishes. A page that looks idle for a minute reads as a stall.

## 2.0.12 (beta)

Makes a bulk run start instead of stalling, measured on your server.

- **A run started by reading the file for minutes before the first subtitle came out.** On network storage a
  read costs about the same whatever its size (measured on fabji's Synology: 12,8 ms per read), so the number
  of round trips is the cost, not the bytes. One episode's extraction pass was issuing 2 058 ranges / 3 553
  reads and spending **46-96 s** before its jobs could begin — which reads as "it takes a very long time to
  start, and then it is fast". Wanted ranges are now **merged across megabytes on such storage**, which turns
  those thousands of reads into hundreds (measured on a fixture built to behave like that share: the same pass
  went from a projected ~42 s of round-trip waiting to **11,3 s**).
- **The decision is measured, not assumed.** The probe now compares the cost *per MB* of small reads against
  one 4 MB read: on a share the big read wins and ranges are merged; on a slow disk (where the bytes are what
  costs) the big read costs the same per MB and the ranges stay tight — verified on both fixtures, no
  regression on the tight path (212 MB / 3 379 reads / 23 s before and after).
- **The storage probe no longer trusts one region.** It reported 0,54 ms per 16 KB on that server — the
  probed region was in the page cache — and a "reads are cheap" verdict is what makes a walker read in
  thousands of small pieces. It now samples four regions spread across the file and the **median** decides.
- **The walk corrects a wrong verdict from its own reads**: if they turn out to cost milliseconds while it is
  reading small windows, it switches to big ones for the rest of the file and says so in the plugin log.
- Trade-off, stated plainly: merging reads more bytes than the subtitles need (an episode's wanted ranges plus
  everything in between). That is the right trade when a round trip costs 13 ms and wrong when bytes are what
  costs, which is exactly what the per-MB measurement decides.
- **A movie with several subtitles analysed its audio once per subtitle.** Two tracks of one 2 h movie were
  started together and each ran the engine against the audio — **141 s each**, both reporting
  `cachedSpeech=False`, because neither had harvested the result yet; on your log that shape appears ten
  times. The audio analysis is per *file*: only one job of a file may run it while that file's speech cache
  is empty, and the others wait for the harvest and then reuse it (verified: one job
  `reference: method=audio why=the audio is this job's own reference (this job does the file's analysis; the
  others wait for it)`, the other `method=speech-cache … (harvested by another job of this file while this
  one waited)`).
- Also here: a **refused Kill or Cancel now says so** ("The kill was refused: HTTP 403 — stopping every run on
  the server needs an administrator account.") and the button drops back instead of staying armed.

## 2.0.11 (beta)

Fixes 2.0.10, which should not have been published.

- **The page no longer stops at "Loading libraries…" in a real web client.** 2.0.10 read the signed-in
  user through a helper that called itself, so with the web client's API object present — which is every
  real Jellyfin client — the page failed at the first library request and left the line on
  "Loading libraries…" for ever. Every browser test before the release ran in a context where that API
  object is absent, so the branch was never taken; the test now defines it, the way a real client does.
- **The page gets its session from the client script** (which runs inside the web client and holds the
  token and the signed-in user) as well as from the web client's own API object, so it no longer depends
  on being able to read the browser's stored credentials.
- **The page says what failed instead of staying unfinished**: each part of the page (status, settings,
  runs, libraries, items) loads on its own, and anything that fails is named on the page. An account
  without administrator rights now reads "settings: HTTP 403" instead of an empty settings form.

## 2.0.10 (beta)

This build is mostly about the plugin's own page in Jellyfin 12 — it ran nothing there — plus one defect
that cost a bulk run four tasks.

- **The plugin page works in Jellyfin 12 again.** It rendered with every control in place and did nothing:
  an inline `<script>` in a plugin page never executes in 12, the script that arrived with the markup is
  fetched but not run, and the script itself stopped part-way through when the web client replaced the
  document. The page's script now lives in its own file the plugin serves, is attached by the client script
  (the path that already works for the ⋮ menu), and starts from the top of the file on the first signal it
  gets. Verified in a browser: status line, libraries, settings and history all load by themselves.
- **The page no longer needs the web client's API object.** It takes its session from the web client's
  stored credentials, builds its own URLs and reads/writes its settings through its own
  `GET`/`POST /SubSync/Configuration`. If the web client never finishes loading, the page says so on its
  status line instead of showing a surface that silently does nothing.
- **The run box now appears for a run the page did not start** (the previous build filled it into a box
  nobody had shown, so a run — and its Cancel control — was invisible unless the page had started it).
- **The Cancel button works for those runs.** It used to do nothing at all when the page was mirroring a run
  someone else started.
- **"Kill all syncing" asks before it stops everything**: the first press asks ("Confirm: kill all syncing",
  with a line saying it includes runs other users started and that nothing has stopped yet), the second
  press within eight seconds does it. Verified: four running jobs, four engine processes gone 5.1 s later.
- **One settings surface.** The dashboard page is now a pointer to the plugin's own page instead of a
  second, partial copy of the same settings.
- **An embedded subtitle whose file has no other text track is no longer rewritten from the audio alone.**
  The result cannot be checked against anything, so the job reports "Unverified — audio-only alignment" with
  the measured shift and writes nothing; the library file is untouched. External `.srt` files still use the
  audio, and now prefer a sibling embedded track as the reference when the file has one.
- **Refusals are reported as refusals, not as failures.** A bulk run of 100 tracks that safely refused two
  tracks used to read as "2 failed".
- **A bulk run no longer loses tasks to a missing extraction file.** The extracted subtitle lived in a job's
  own temporary directory, and a job deletes that directory when it finishes — so a job that started later
  (or re-entered) failed with `Could not find a part of the path …/subtitle_15.srt`. The directory is now
  shared per file and goes when the last job reading it is done: the run that lost four tasks finishes with
  zero failures, and a finished run leaves nothing behind in the cache.
- **A fetched reference stays until every job using it has finished**, `Kill` also stops the extraction
  pass's reads, and a reference-derived shift beyond the configured limit (default 30 s) is refused instead
  of written.
- **Measured, cold cache, one file per setting**: whole series 98 Completed + 2 Refused in 35 s (the
  refusals are the 30 s limit above); the 2.38 GB episode on slow network storage at 4 workers finishes 50
  tracks with no failures, and `workers=8` is 2.8× faster than `workers=1`.

## 2.0.7 (beta)

- **A shared pass now serves the tracks it used to skip.** Tracks whose index names the cluster but not
  the block's position inside it were left out of the pass, and their jobs then read the file again on
  their own - one episode cost two full passes, which is what `tracks=24/30` in the log meant. Those
  tracks are now collected by walking the cluster the pass is already holding, so their cost is parsing
  rather than reading. (Tracks with no index entry at all still fall back to their own extraction.)
- **One read per cluster instead of five.** The pass reads the byte range its wanted blocks actually
  occupy - computed from the positions the index gives - rather than hunting through the cluster in
  fixed windows. Same bytes, a fifth of the reads, which on a share that answers a read in milliseconds
  is most of the time.
- **Fixed the storage probe again.** 2.0.6 read the same offset three times to time the storage, which
  measures the page cache (the second and third reads come out of RAM) and reported 0.00 ms per read on
  a share that answers a real read in milliseconds - so that server chose 4 KB pieces and read 611 MB in
  10,782 reads for 42 s a pass. It now steps forward at three distinct offsets, which is the pattern a
  walk uses.

## 2.0.6 (beta)

2.0.5 made extraction slower on a real server, and this puts that right and adds the thing that
actually makes a library fast: extraction is paid once per file, not once per run.

- **Fixed (regression from 2.0.5): a read at a known position pulled a whole-file window.** Files whose
  cue index names the position of each subtitle block should be read a block at a time - a few
  kilobytes each. 2.0.5 gave the walker a 4 MB window and let that window leak into those reads, so
  every cue read 4 MB: 779 cues moved **3.2 GB** to collect ~50 KB of text, at 68 s per episode, where
  2.0.4 had read 611 MB in 42 s. Known-position reads now set their own small window, the shared pass
  reads a cluster-sized region instead of the file, and the storage probe reads sequentially rather
  than jabbing at three random offsets (which measured 11 ms per 16 KB on a share whose own walk was
  running at ~4 ms per read, and so talked the walker into the wrong choice).

- **Extracted subtitles are now cached per file, so later runs extract nothing.** This is the answer
  the ecosystem settled on, because no extractor avoids reading the file: Jellyfin's own issue #17667
  ("FFmpeg generally needs to read the complete file to extract embedded subtitles") measured its own
  extraction holding a 1 GbE link for three minutes, and mkvextract is documented as having to churn
  through the whole file. What you can do is pay it once. Measured locally on a 2.5 GB file with eight
  subtitle tracks: the first run reads the file and takes 6.07 s end to end, the second run logs
  `extract: method=cache ... (no read: this file's subtitle was extracted on an earlier run)` and takes
  2.04 s, with extraction contributing nothing. Entries are keyed by the media file's path, size and
  modification time, so a re-encoded file cannot be served a stale subtitle; they expire after 120 days
  or when the cache passes 512 MB, and the Settings tab's "Clear cache" button removes them along with
  everything else.

## 2.0.5 (beta)

Extraction read the whole file and did it several times per episode. This removes both, and it does
not assume anything about anyone's storage.

- **Fixed: the reader's window was capped at 64 KB, which is why extraction read the file through.**
  The walker already skipped a block by jumping over its payload - but with a 64 KB window nearly every
  jump landed outside the window, so the next window was read again and the windows tiled the file end
  to end. Measured on a real server: 611 MB read and 10,779 read calls for one 611 MB episode, 42-46 s
  per pass, and the same file read twice back to back.

- **The window is now sized from a measurement of the storage, not a constant.** A walk reads a few
  probes, keeps the fastest, and decides: if a read costs a millisecond or more (a network share), the
  clusters are read in pieces up to 4 MB, so an episode costs a handful of reads instead of ten
  thousand. If reads are cheap (a local disk), it reads only the block headers and skips the payloads,
  which moves a fraction of the bytes. The measurement is taken once, lazily - a file whose index names
  its blocks is read by probing exact positions and never pays for it - and it is printed in the log:
  `extract: storage 0.03 ms per 16 KB read -> walk window 4 KB (reads are cheap here, so only the block
  headers are read)`. Two installs with different hardware will therefore report different decisions,
  which is the point: nothing here is tuned to one server.

- **Measured on the test fixtures, on the file shape that was slow** (cue points for the subtitle track
  but no block offsets, which is what that server's files have): 0.60 MB and 17 reads before, 0.10 MB
  and 20 reads after. On the local-disk branch that is a 6x cut in bytes; on the share branch the same
  file costs ~150 reads instead of 10,779.

- **Fixed: every pass was logged twice.** The shared pass wrote its own line, and the job then wrote a
  second line with the same numbers under the inner method's name, which read like a second pass had
  run. One pass, one line now.

- The pass line also reports `blockOffsets` (whether the file's index says where its subtitle blocks
  are) and the reuse line reports `cacheLeft` (how many languages are still waiting in the pass's
  cache), so a run's log shows whether the work a pass did is reaching the jobs that need it.

## 2.0.4 (beta)

- **Fixed: the Sync tab was unclickable, and a Save button appeared on it.** Removing the "Reference
  sanity limit" field in 2.0.2 left its closing `</div>` behind. That single stray tag closed the page's
  container early, so everything after it sat outside the layout Jellyfin expects - which is why the
  controls stopped responding and Jellyfin's configuration Save button turned up on the Sync tab. My
  mistake, introduced by me, and the reason you could not use the page.
- The same stray tag was found in the Settings tab, where it had been since well before today (it was in
  2.0.1.0 as well) and had been quietly breaking that page's structure. Both pages now close every tag
  they open.
- The test suite now parses both pages and fails if their tags do not balance. It did not check this
  before, which is exactly why a one-character defect could take the whole interface down.

## 2.0.3 (beta)

- **"Clear cache" now clears every cache, not just one.** It used to empty the audio analysis only,
  which left the reference subtitles behind - the exact thing that had been giving you wrong offsets.
  It now removes the audio analysis, the references of the current run, and the scratch folders of
  finished jobs, and reports what it removed. Safe at any time: everything is rebuilt when it is next
  needed, so clearing only costs time. The Settings tab says so, and the label is "Cached data" rather
  than "Cached audio analysis".
- A running job's scratch folder is never touched by that button - only folders whose job is gone.

## 2.0.2 (beta)

Two fixes to the same mistake: 2.0.1 answered a bad alignment by refusing the job, which is worse than
useless - the plugin exists to sync subtitles, and "which usually means that reference track is
mis-synced" blames the user's files for our bug.

- **Fixed: a forced/signs track is never used as the reference.** A signs track holds a handful of lines
  over a whole episode, and aligning full subtitles to it drags every language onto the same wrong
  offset - measured on a real server as five tracks moved by the same +57.5 s. The reference picker now
  skips forced tracks, and a reference that turns out to hold too few cues is thrown away at extraction
  time and the job continues **against the audio** instead. The sync still happens; it just stops being
  built on a ruler that cannot measure anything.
- **Fixed: nothing is refused.** 2.0.1's "Reference sanity limit" and its refusals are gone, along with
  the setting. A clamped offset (one that lands on "Maximum offset") is written and annotated in the
  job's result rather than failing the job, and an unusually large shift against a subtitle reference is
  reported as something to check, not as a refusal. The only refusal left is the pre-existing one for an
  unrequested framerate rescale, which is a different class of wrongness.
- The log still names the reference for every job, and now says when it was discarded for having too
  few cues.

## 2.0.1 (beta)

Fixes around the subtitle a sync is aligned against (the "reference"), all found by reading a real
server's plugin log.

- **A reference subtitle is no longer kept between runs.** It is extracted into the run's own cache
  directory, shared by the other subtitles of the same file while they are queued, and deleted as soon
  as that file's last subtitle is done (an interrupted run cannot leave one behind either — the
  directory is cleared at startup). Why it matters: a reference taken from a sibling subtitle track
  inherits that track's error and every other track of the file inherits it in turn. On a real server
  one such reference moved five language tracks by the same +57.5 s and all five were written as
  successes; clearing the cache did not change the result, because the wrong reference was rebuilt
  identically every time. The audio analysis (the expensive one) is still cached as before.
- **Fixed: a result pinned to "Maximum offset" is refused, not written.** A subtitle that came out at
  exactly +60000 ms against a 60 s ceiling was clamped, not fitted — the file was still wrong while the
  job reported success. Such a job now fails with the measured numbers and the source is untouched.
- **Added: "Reference sanity limit" (default 30 s).** When a subtitle is aligned against another
  subtitle track and the shift exceeds this, the reference is treated as mis-synced and the job is
  refused with the track and the shift named, instead of writing a wrong file. Sync against the audio
  is not affected. Set it higher only if the subtitles really are that far out.
- **Fixed: queueing no longer crawls.** The per-file checks the scheduler makes while holding the queue
  lock are now answered from memory instead of hitting the media share on every planning pass. Measured
  on a real server before this change: 240 queued tasks spent 105-424 s queueing, with single tasks
  stalled 32-40 s, and the log showed a single task spending 40326 ms of its 40331 ms in that wait.
- **Added: the log names the reference.** Every extracted reference now reports which track it came
  from and how many cues it has ("track s:1, 7 cues"), and every refusal names the track and the shift.
  Without that line, a wrong reference was invisible.

## 2.0.0 (beta)

**Jellyfin 12.0 support.** The plugin is rebuilt for the new server generation; it is the
2.x line from here on. Jellyfin 12.0 removed the ability to load plugins built for 10.11,
so this is a replacement build, not an in-place update.

- **Jellyfin 12.0 / .NET 10.** Retargeted to `net10.0` and built against Jellyfin 12.0
  (`targetAbi 12.0.0.0`). Jellyfin 12 refuses to load a 10.11 build, so nothing carries
  over from the old zip.
- **Fixed: the plugin's own API calls were rejected on Jellyfin 12.** 12.0 disables the
  legacy authorization mechanisms, which means the `X-Emby-Token` header the pages used is
  no longer read: every call came back **401** and the Settings tab, the history and the
  detail-page dialog showed nothing. All calls now use the same
  `Authorization: MediaBrowser Token="…"` header jellyfin-web sends, and the debug-log
  link uses `?ApiKey=`. Both forms are also accepted by 10.11, so one implementation covers
  both.
- **Fixed: the debug-log link on the main page never rendered.** `subsyncMain.html` called
  two helpers that only existed inside the injected client script, so the line threw and the
  rest of that status update (speech-cache line and the engine badge) never ran.
- **Note for Jellyfin 10.11 users:** 1.1.x is the last build for 10.11. The new version
  declares `targetAbi 12.0.0.0`, so a 10.11 server does not see it and is not offered a
  broken update — it keeps working on the installed 1.1.x.

## 1.1.0 (beta)

- Added: multi-select in the library browser without checkboxes — click one movie or
  series, then **shift-click or right-click** any other rows to add or remove them
  individually (picks do not have to be adjacent). Right-click is the primary gesture;
  shift-click works too and no longer drag-selects the page text. The **Select all** link
  under the search box stays scoped to the current library + search view, and once
  anything is picked a full-width action row appears with a **Sync N files** button, a
  **Subtitles** dropdown and a count note. The dropdown lists the languages actually
  present in the picked files (with track counts), gathered by a background scan that
  reads the same cached subtitle lists the queue build uses; **several languages can be
  picked at once** (they show as removable chips next to the dropdown, and *All languages*
  clears them). Language codes are folded onto one canonical entry per language, so
  `sv`, `swe`, `sv-SE` and `Swedish` all become a single "Swedish" option. Series always
  expand to all their episodes.
  While the queue is assembled the button reads "Working…" and the run line shows
  "Reading subtitles… N/M files", then the batch joins the server queue (behind a running
  batch if one is streaming). Movies contribute their subtitle tracks; series contribute
  one track per language per episode (external preferred).
- Changed: image-based subtitle tracks (Blu-ray **PGS**, DVD **VobSub**, **DVB**, **XSUB**)
  are no longer listed anywhere — library rows, detail-page track lists, language
  dropdowns and the scheduled sweep skip them, so they can't be picked and fail. Only
  alignable text subtitles are offered.
- Added: a **global language filter**. In the dashboard **Settings** tab (own section,
  "Only sync subtitles in these languages") or the plugin config page (comma-separated),
  list the languages you want and everything else is hidden from every list and skipped by
  the sweep. Codes and names are interchangeable — `sv`, `swe`, `sv-SE`, `Swedish` all
  match the same language, as do `chi`/`zh`/`zho`/`Chinese`. Empty list = sync every
  language; while a filter is set, text tracks with no language at all are skipped because
  they can't be matched.
- Added: **shift+right-click** a row to select the whole range from the anchor (the last
  plain-clicked row) — the previous range behaviour, now opt-in so single-row right-click
  picking stays exact.
- Added: a small spinner next to the language dropdown while the picked files are being
  read ("reading subtitle lists… N/M files"), and the note now counts **subtitle tracks**
  once a language is picked — e.g. `280 files selected — series sync all their episodes —
  1,240 German subtitle tracks to sync` — with the button reading `Sync 1,240 subtitles`
  in that case. Counts are only shown once the scan is complete, so the number always
  matches the queue that gets built.
- Added: **multi-subtitle modes** — choose in the Settings tab (and per run in the library
  action row): **Normal** (one at a time, audio analysed every run), **Parallel** (several
  subtitles at once, `Parallel workers` 1-8) or **Fast** (the audio is analysed once per
  media file and the speech signal is reused for its other subtitles — identical results,
  ~2-3x less work per extra subtitle). The cached speech signal lives in the plugin's state
  directory (~2 KB per file) and is keyed by file size/mtime, VAD method and ffsubsync
  build, so a replaced file never reuses stale data. Nothing is ever written into media
  folders.
- Added: **one-time config migration** to the automatic strategy. Installs that stored an
  explicit mode (the pre-1.1.0.16 default was `normal`) are moved onto `auto` the first time
  the new build loads, and it is saved immediately — so an upgrade gets the new behaviour
  without opening the settings page. Manual overrides chosen afterwards are respected.
- Fixed: parallel runs on a **single-volume library effectively ran one task at a time**.
  The per-volume heavy-read gate allowed exactly one heavy reader, and with an uncached
  library every first analysis is heavy — so four workers sat idle. The gate now takes a
- Fixed: the per-worker progress rows vanished under the automatic strategy — the panel
  keyed on an explicit parallel mode, and `auto` is resolved only once the run starts. It
  now shows whenever anything is running, and the run line names the strategy and worker
  count (`parallel + reusing audio analysis · 2 workers · task 12/284: …`).
- Changed: extraction now **says what it is doing**. The phase names the method before the
  work starts — `Extracting subtitle with the Matroska cue index` / `with the MP4 sample
  table` / `with ffmpeg — reading 18432 MB, up to 20 min` — and while ffmpeg works the phase
  tracks its position (`Extracting subtitle with ffmpeg — 43% of the file read`), driven by
  ffmpeg's `-progress` stream, with elapsed time per worker in the run box.
- Changed: the library browser now reads subtitle lists in **bulk** (`POST
  SubSync/Subtitles/Batch`, 25 items per request, series expanded server-side) instead of one
  request per file and per episode — a library-wide selection went from hundreds of
  sequential round-trips to a handful.
- Added: **automatic sync strategy** — the mode picker is gone from the Sync tab. `auto`
  (now the default) decides per run: one subtitle on one file stays sequential; several
  subtitles of one file analyse the audio once and reuse it; several files run in parallel
  with each file's analysis reused. The explicit modes remain as a troubleshooting override
  in Settings.
- Added: storage-aware scheduling — later removed in 1.1.0.20 as unnecessary
  (`MediaVolume.Of` maps a path to its mount point/device), so parallel workers sharing a
  disk no longer stall together; tasks whose audio analysis is already cached read nothing
  and still run fully in parallel. Several subtitles of one file may now share a wave once
  that file's speech signal is cached.
- Added: **MP4/MOV index extraction** — embedded `tx3g`/`mov_text` subtitles are read
  through the file's sample table instead of demuxing it (verified against ffmpeg: 200/200
  cues, identical text, 0.000 s timing difference). The extraction chain is now Matroska
  cues → MP4 sample table → ffmpeg, each skipped method logging its reason.
- Added: **extraction timeout** (`ExtractionTimeoutMinutes`, default 20) — a slow or stuck
  ffmpeg fallback now fails with a message naming the file size, the timeout and why the
  indexed readers could not be used, instead of showing a progress bar frozen at 5%.
- Fixed: **embedded subtitles could be aligned against themselves.** ffsubsync's default
  detector (`subs_then_*`) prefers a file's embedded text subtitle as the speech signal —
  but when the subtitle being synced *is* that embedded track, the alignment can only
  return zero, so the sync silently did nothing ("already in sync"). Measured: a track 6 s
  out of sync came back unchanged (offset 0.000); the same file with the reference pointed
  at its other text track was corrected by exactly −6.000 s. Embedded syncs now pass
  `--reference-stream s:N` for another text track when the file has one, and `a:0` (audio)
  when the track is alone — the speech-signal choice is also part of the speech-cache key.
- Changed: **Cancel now reports and escalates.** Pressing Cancel drops queued tasks and
  then tells you what happened: how many tasks were dropped and whether any run is still
  working (a running ffsubsync cannot be interrupted). While processes are still alive the
  button becomes a red **Kill all syncing**, which terminates the running ffsubsync/ffmpeg
  processes and empties the whole queue across every batch (`POST SubSync/Kill`); the log
  and phase line report how many runs were killed and whether any are still shutting down.
  `GET SubSync/Active` backs the reporting.
- Added: **Ultimate mode** — parallel plus audio reuse: several media files at once, each
  file's speech analysis computed once and reused by its remaining subtitles. Same
  alignments as every other mode; the only difference is throughput.
- Added: **per-worker progress** in Parallel and Ultimate modes — the run box now shows one
  row per worker (file, phase, own progress bar) instead of only a single combined line.
- Added: **fast embedded extraction for Matroska** — embedded text subtitles are read via
  the file's cue index instead of demuxing the whole container. On a 1.7 GB test file that
  is 202 ms instead of 2.32 s (the fallback path scales with file size: minutes over a
  NAS), with the same cues and identical text. Falls back to ffmpeg automatically for
  anything it cannot handle (image codecs, laced blocks, compressed blocks, no cue index)
  and can be disabled with `FastMkvExtraction`.
- Changed: parallel waves now cover **different media files only** — several subtitle
  tracks of the same file are never processed simultaneously, since that would have two
  processes reading the same file and repeating the same audio analysis.
- Changed: default parallel workers 2 → **4**.
- Added: speech-cache housekeeping — entries unused for 30 days are pruned automatically
  and the cache is capped at 250 MB; the Settings tab shows its current size with a
  **Clear cache** button (`POST SubSync/SpeechCache/Clear`). Each entry is a few KB and
  lives in Jellyfin's plugin data directory, never in a media folder.
- Changed: the Settings language picker no longer uses a native `<datalist>` (its popup
  opened as an enormous list that could not be sized) — it is now a compact, scrollable
  suggestion box filtered as you type.
- Fixed: selecting a series in the library showed **no languages at all** in the row's
  Subtitle picker unless a specific season was chosen — the language scan started before
  the row existed; it now starts right after the row is rendered. The picker also folds
  language codes together (`sv` / `swe` / `sv-SE` / `Swedish` are one entry, shown with
  friendly names and per-track counts).

## 1.0.10

- Fixed: the sync outcome (offset in ms / framerate ratio) is now shown in the live
  progress log and in expanded history, not just in the history log builder.

## 1.0.9

- Added: every successful sync reports what it actually did — signed offset in
  milliseconds (`+79 ms offset`) and, when a framerate mismatch was corrected, the
  fitted `framerate ratio 1.0004x` with the cumulative drift it fixed.
- Fixed: a subtitle that ffsubsync judged already in sync (shift under its 3 s
  threshold) used to fail with "output file was not created"; it is now a success
  reported as "already in sync — no change needed".

## 1.0.8

- Added: syncs can be queued while another run streams — stack as many movies, seasons
  and series as you like; each joins the server queue and the UI advances to the next
  queued run automatically.

## 1.0.7

- Fixed: embedded subtitle extraction on files that mix embedded and external tracks
  (`Failed to set value '0:4' for option 'map'`). The real container stream is now
  located by probing the file with ffmpeg instead of trusting Jellyfin's stream index.

## 1.0.6

- Fixed: embedded tracks on files with both embedded and external subtitles
  (`Failed to set value '0:s:2' for option 'map'`).

## 1.0.5

- Added: series/season scopes select subtitles by language (friendly name + track
  count); duplicate languages per episode are merged, preferring the external track.

## 1.0.4

- Added: language filter for series/season syncs.
- Removed: the "first track only / all tracks" toggle for series — the language picker
  makes it redundant.

## 1.0.3

- Added: library sweep as a native Jellyfin Scheduled Task ("Sync subtitles (library
  sweep)") with a persistent skip/fail cache, so repeat runs only touch new or changed
  subtitles. Ported from Marnalas/jellyfin-subsync (MIT).

## 1.0.1 – 1.0.2

- Added: episode-accurate batch counters and task titles (episode x/y, task n/m).
- Change: video files are never modified — embedded subtitles are extracted and saved
  as new external sidecar files; the remux path was removed entirely.
- Docs: architecture, API reference and configuration notes synchronised with the code.

## 1.0.0

- First public release: bundled self-contained ffsubsync (linux-x64), zero setup on
  Docker, detail-page "Sync Subtitles" action, dashboard library browser with per-track
  selection, copy-by-default output (`-SYNCED.srt`, original untouched), server-side
  FIFO batch queue with history that survives page reloads.## [1.1.0.54]

### Fixed
- **Subtitles are no longer time-scaled to fit, which was the de-sync.** ffsubsync corrects a framerate
  mismatch by default on the bundled 0.5.1 engine, and it infers the ratio from the ratio between the
  reference duration and the subtitle's own span. A subtitle whose last cue sits a few percent outside
  the video - an extra scene, a different credits roll, a longer release - therefore reads as a framerate
  mismatch, and the whole file is rescaled. Because the error grows with every cue, it is not a small
  miss near the start: it is a few seconds in and minutes at the end.

  Measured with the bundled engine, input span 4.17% longer than the reference:

  ```
  default (what the plugin did until now):           0.960x scale, -51.9 s shift, -104 s of drift
  --skip-infer-framerate-ratio alone:                0.960x scale, -51.9 s shift, -104 s of drift
  --no-fix-framerate alone:                          0.960x scale, -51.9 s shift, -104 s of drift
  --no-fix-framerate + --skip-infer-framerate-ratio: ratio 1.0000x, offset only
  ```

  Both flags are needed - either one alone still rescaled. They are now always passed unless framerate
  correction is explicitly switched on, and `--gss` only accompanies that switch (it used to be a
  separate tick box that could pass `--gss` on its own).

- **A rescaled result is refused instead of written.** Even with correction on, the measured ratio has
  to be a real framerate pair (25/23.976, 25/24, 24/23.976, their inverses, half/double) and the shift
  has to stay within the offset bound handed to the engine. With correction off, any ratio away from
  1.0 is refused outright. Nothing destructive reaches the library: the output is discarded, the source
  subtitle is untouched, and the job says exactly what it measured and why it stopped.

  ```
  job <id> REFUSED: measured +65579 ms offset at start · framerate ratio 1.0427×
    (≈+117408 ms cumulative drift) over a 44.0-minute file — framerate correction is off;
    nothing written, source untouched, file=/media/...
  ```

- **Cancel batch now stops the running jobs of that batch, not only the queued ones.** Pressing it
  while eight runs were analysing speech left them all running, which reads as a cancel that does
  nothing. Those jobs are cancelled too, and the phase each was in is recorded.

### Added
- **The engine command line is logged as it was passed.** It logged
  `args=System.Collections.Generic.List\`1[System.String]` - the *type* of the argument list, never its
  contents - so nothing in the log could prove which flags were in force. It now writes the argv, which
  is also how the flags above can be verified on a real run.
- **Cancel and Kill write to the plugin log.** Both only logged through Jellyfin's own logger, so the
  file handed over for debugging could not show whether a kill had arrived, what it cancelled or what
  survived it:

  ```
  KILL requested: 59 queued cancelled, 8 run token(s), 8 process tree(s) killed, 0 survivor(s)
    · still reporting: 1a2b3c4d=Analyzing speech (The Helicopter Heist - S01E01)
  cancel batch <id>: 61 queued cancelled, 8 running stopped (phases: 1a2b3c4d=Extracting subtitle)
  ```

- **`fixFramerate` is in the startup line**, so a log says whether correction was on for the run that
  wrote it.

### Settings
- **Correct framerate mismatch (advanced)** - off by default, in the Settings tab. Only for subtitles
  known to come from a different framerate; leave it off otherwise.

## [1.1.0.53]

### Fixed
- **A queued task is no longer shown as a failure.** Opening a batch while it was still being queued
  listed every task as `FAIL <title> — Queued`, which reads as a run that failed instantly. Nothing had
  failed: those tasks had not started. Only finished tasks are reported (`OK`, `SKIP`, `FAIL`); the rest
  are counted (`(37 tasks still queued or running)`), and the batch view in History is fixed the same way.

- **This plugin's own synced sidecars are no longer offered as tracks.** A sidecar is named
  `<video>.SYNCED.<lang>.srt`, and Jellyfin reads the marker as a *language* - so every track list
  carried a language called "SYNCED", and a season sync queued those files next to the very tracks they
  were produced from. The batch therefore did the same work twice, and the second pass wrote over the
  file the first had just written (which is also why a "Den osannolika mördaren" season batch came to
  147 tasks). The marker is now recognised by name (`SrtWriter.IsSyncedSidecarName`, including the
  legacy hyphen form) and those files are left out of the lists the UI offers. The originals are still
  listed, and syncing one writes that sidecar again, so nothing becomes unreachable.

### Added
- **The enqueue path reports where its time goes.** Queueing has to be fast: the scheduler can only
  start what is already in the queue, so a batch that trickles in looks exactly like a plugin that
  refuses to parallelise - which is what a run on 1.1.0.51 did (twelve tasks in 13 ms, then about 5.3 s
  per task once a job was running). Every task is timed part by part and the slowest one is named:

  ```
  batch <id> queued: tasks=147 mode=auto label='...' totalMs=524374 slowestTaskMs=20050
    (index 53: gap=12 ms; item=1 ms, sources=19980 ms, settings=2 ms, log=1 ms, total=19996 ms)
  enqueue slow: item=1 ms, sources=19980 ms, settings=2 ms, log=1 ms, total=19996 ms stream=43 video=...
  ```

  `gap` is the time between two tasks inside the loop, so a cost that is not inside the task is visible
  too.

## [1.1.0.52]

### Fixed
- **A queued task is no longer reported as a failure.** Opening a batch while it was still being
  queued listed every task as `FAIL <title> — Queued`, which reads as a run that failed instantly and
  it was not: the tasks had not run yet. Only finished tasks are reported now (`OK`, `SKIP`, `FAIL`),
  and anything still waiting is counted instead: `(37 tasks still queued or running)`. The batch view
  in History had the same flaw and is fixed the same way.

### Added
- **The enqueue path reports where its time goes.** Queueing has to be fast: the scheduler can only
  start what is already in the queue, so a batch that trickles in one task every few seconds looks
  exactly like a plugin that refuses to parallelise - which is how a real run on 1.1.0.51 behaved
  (twelve tasks in 13 ms, then about 5.3 s per task once a job was running). Every task is now timed
  part by part, and the batch line names the slowest one:

  ```
  batch <id> queued: tasks=147 mode=auto label='...' totalMs=524374 slowestTaskMs=20050
    (index 53: gap=12 ms; item=1 ms, sources=19980 ms, settings=2 ms, log=1 ms, total=19996 ms)
  enqueue slow: item=1 ms, sources=19980 ms, settings=2 ms, log=1 ms, total=19996 ms stream=43 video=...
  ```

  `gap` is the time spent between two tasks inside the loop, so a cost that is not inside the task is
  still visible. With that line the slow part can be named instead of guessed at.

## [1.1.0.51]

### Changed
- **A file is read once and every queued subtitle of it comes out of that one pass.** Until now each
  language of an episode walked the same clusters again: a 50-language episode paid the cluster reads
  fifty times. The first job for a file now extracts every embedded track that is queued for it in a
  single pass, caches the results, and the jobs that follow reuse their track instead of reading the
  file at all. Tracks the pass cannot serve (no cue index, no block offsets, not text) fall back to
  their own extraction, and a pass is capped at 48 tracks so a very large batch does not hold every
  track's text in memory at once.

  Measured on the generated fixture with three subtitle tracks in one file:

  ```
  one pass extracts every requested track          [3 of 3 tracks]
  track 0 is identical with and without sharing    [shared 813 chars vs single 813 chars]
  track 1 is identical with and without sharing    [shared 803 chars vs single 803 chars]
  track 2 is identical with and without sharing    [shared 803 chars vs single 803 chars]
  sharing one pass reads less than separate passes [shared 27 reads vs 51 separate (53%)]
  the shared pass returns every track's cues       [10 + 20 vs 30]
  ```

  Three tracks are half the reads; the saving grows with the number of languages, because the cluster
  header is read once for all of them rather than once for each.

### Added
- `MkvSubtitleExtractor.TryExtractMany` extracts several text tracks of one file in a single pass, with
  per-track results and the cost of the whole pass in its stats.
- The fixture generator can write several subtitle tracks (`--sub-tracks N`), which is what the checks
  above run against.

### Notes
- The task result says which of the two it got: `read through the container index (seekhead-cues), N ms`
  for the job that did the pass, `reused from the pass that read this file for another subtitle` for the
  ones that did not, and the plugin log records the pass itself as
  `extract: method=shared-pass ... tracks=N/M ... alsoBlocks=...`.

## [1.1.0.50]

### Fixed
- **Batches queue in milliseconds instead of minutes, so the workers are actually used.** The
  blocking subtitle extraction ran on a thread-pool thread, and it blocks for tens of seconds at a
  time reading the media file. Eight of those consumed the pool, and the request still queueing the
  rest of the batch was starved behind them - which is why the plugin log from a real run shows a
  59-task batch taking 253 s to enter the queue, in groups of one to eight tasks a few seconds apart:

  ```
  03:27:33.940  queued: ... stream=38 language=heb      ← burst of 10 tasks in 21 ms
  03:27:37.833  dispatch: starting 1, running 0, limit 8, queued 10, batch 0948500d...
  03:27:37.834  queued: ... stream=8  language=hrv      ← 3.9 s later, one task
  ```

  The scheduler can only schedule what is in the queue, so `starting 1 of 8` was the honest answer to
  a queue that held one task. The extraction now runs on its own thread (`TaskCreationOptions.LongRunning`),
  which costs nothing and leaves the pool for request handling. The earlier `File.Exists` removal on
  the enqueue path was not the cause and is kept only because queueing should not touch a busy share.

- **The pump wake signal can no longer be swallowed.** The wake semaphore was bounded to one signal,
  so several wakes collapsed into one and a consumption could leave a completion unnoticed: dispatch
  lines sat 19 seconds apart with eight slots idle. It is unbounded now.

- **The progress panel no longer goes stale until the page is reloaded.** It only updated while *that
  page* was streaming a batch it had started itself, so a run started from the detail view left it on
  "Queued — waiting for earlier runs to finish…" indefinitely. A heartbeat now mirrors whatever the
  server is running, whoever started it, and falls back to `Idle — last run finished at N/M` when
  nothing is queued.

### Changed
- The run line states how many subtitles are done out of how many: `parallel · 7/8 workers · 35/323`,
  with `· N failed` when any failed. Episode numbers said where a run was, not how much was left.
- The "Queued — waiting for earlier runs to finish…" wording is gone; a queued run reads
  `N/M · waiting to start` or names how many subtitles are running from an earlier run.
- The detail dialog keeps polling through a failed request instead of giving up on the first one and
  leaving stale numbers on screen; it reports `Reconnecting… (n)` and only stops after ten.

## [1.1.0.49]

### Fixed
- **The worker panel shows one row per worker that is actually working again.** Drawing every
  configured slot padded the panel with "idle -" rows and empty bars, so a two-worker run became eight
  lines and the tasks doing the work were the hard part to find. Each row keeps what the old layout
  carried - which subtitle, what phase it is in, and how far along it is - and nothing else.

### Changed
- Slimmer progress bars: 3 px for the run bar and the per-worker bars (was 5 px, and 7 px before that).
- Less text: no per-row elapsed time, and the batch phase line stays hidden while the worker rows are
  showing the same phase. The run line stays short - strategy badge, `busy/limit workers`, and the
  position (`episode 7/136`, or `task 12/40` for a single episode).

## [1.1.0.48]

### Fixed
- **A batch is queued without touching the media share, which is why runs behaved as if they were
  single-threaded.** `EnqueueSync` stat'ed the video file once per task, and while a job was already
  reading that share each stat took seconds. The plugin log from a real run shows the shape of it - a
  50-task batch taking over a minute to enqueue, in groups of 1-13 tasks about 9 seconds apart:

  ```
  02:59:56.609  queued: job=... stream=2  language=ara   ← 12 tasks in 23 ms
  03:00:06.472  queued: job=... stream=33 language=hun   ← 10 s later
  03:00:15.972  dispatch: starting 1, running 0, limit 8, queued 15, ... 0 running
  03:00:15.978  queued: job=... stream=8  language=kor
  03:00:24.969  queued: job=... stream=9  language=msa   ← 9 s later
  ```

  The scheduler can only schedule what is in the queue, so with one or two tasks arriving at a time it
  reported `starting 1 of 8` for a run that had 39 tasks waiting to be created. Validation now uses
  Jellyfin's cached metadata only, and the file is checked once per job when it runs (same clear
  message as before). The batch log line now states the enqueue duration and the slowest task, so a
  queue that fills slowly is visible instead of looking like a scheduler that refuses to parallelise.

- The wave planner carried an unused variable that read like a hidden throttle on heavy jobs; removed.

## [1.1.0.47]

### Added
- **The plugin writes its own log file**, `<jellyfin-data>/subsync/logs/subsync.log`, rotated at 4 MB
  with three older files kept. Jellyfin's server log is shared with everything else, rotates on the
  server's schedule and needs shell access to read; a run that behaves badly - a subtitle that took
  two minutes to extract, a batch that never used the configured parallelism, a track that was quietly
  swapped - needs its own numbers, in one file, from one run.

  What it records: the startup facts (build, settings file, worker setting), every queued task with its
  language/external/forced flags, the **dispatch decision** including *why* fewer jobs started than the
  limit allows, each extraction with method, milliseconds, **bytes read, read calls, cues, clusters**
  and the reason when the index reader refused a track, each ffsubsync invocation with its arguments,
  cached-speech state and exit code, the measured change, the file written and its size, and failures
  with their stack traces. Entries carry a UTC timestamp and a level (INFO/WARN/ERROR), and a log write
  can never fail a sync.

- **The log is readable from the interface**: the Settings tab shows the path and size with an `open`
  link (`GET /SubSync/Log` returns the tail as plain text), so handing a log over for debugging does not
  require shell access to the server.

## [1.1.0.46]

### Fixed
- **WebVTT tracks no longer go through ffmpeg.** The container reader's text-codec list was missing
  the codec IDs WebVTT actually uses - ffmpeg writes `D_WEBVTT/SUBTITLES`, mkvmerge writes
  `S_TEXT/WEBVTT` - so every VTT track was rejected by the index reader and demuxed instead: a
  whole-file read per subtitle. WebVTT in Matroska is stored text-per-block exactly like
  `S_TEXT/UTF8` (the timing is in the block header, not the payload), so it is read through the
  index now. Measured on a muxed fixture, same 40 cues: **58 ms, 0.26 MB, 67 reads** through the
  index versus a full demux. VTT markup is handled too: voice/class spans, inline cue timestamps and
  HTML entities are stripped, and a payload that carries its own timing line keeps only the text.

### Added
- **Every worker slot is drawn, busy or idle.** The panel showed nothing while a batch waited behind
  another run, which read as "nothing is happening". It now shows one row per slot (up to the
  configured count) filled from whatever the server is running, so the busy ones have their own bar
  and phase and the rest are visibly idle - the answer to "is it actually running in parallel". The
  queued line names what it is waiting for (`Queued - 5 tasks still running from an earlier run`).

- **The extraction cost travels with the result.** Each completed task now states how its subtitle was
  obtained: `read through the container index (matroska-cues), 31 ms` or `demuxed with ffmpeg,
  96000 ms`. Which reader ran and what it cost is the difference between a fast sync and a slow one,
  and it was previously only in the server log.

## [1.1.0.45]

### Fixed
- **A forced (signs/text) track is never picked automatically any more.** Reported from real use: a
  synced Norwegian sidecar came out with two cues for a whole episode. The file carried that
  two-cue forced track *and* a full WebVTT track in the same language, and where two tracks shared a
  language the pick was "external file preferred, otherwise the first one" - so it took the signs
  track and produced a valid but nearly empty subtitle. Tracks are now ranked: **not forced before
  forced**, then an external file before an embedded one. Both surfaces use the same ranking (the
  browser mode's scope build and multi-select build, and the detail-view dialog).

- **Forced tracks are visible instead of indistinguishable.** The language list counts them
  separately (`Norwegian Bokmål - 24 episodes, 26 tracks, 6 forced`), and the single-video dialog
  marks a track as `embedded - forced`. Previously nothing in the interface said which of two
  same-language tracks was the signs track.

- **A synced track with a handful of cues is called out.** A full episode subtitle carries hundreds of
  cues, so when one carries fewer than twelve over a video longer than ten minutes the log says so and
  the job's outcome ends with `only 2 cues in a 44-minute file - looks like a forced/signs track, not
  the full subtitle`. The output itself looks perfectly normal, which is why this mistake is invisible
  without it.

### Note
- Files already written from a forced track keep the wrong content until they are synced again; a new
  run overwrites them (the name is derived from the video and the language, and the pick is now the
  full track).

## [1.1.0.44]

### Changed
- **The progress display is quieter.** Reported as messy and overwhelming, so:

  - the bars are **3px instead of 7px** (worker bars were 5px), with square ends so they read as thin
    rules rather than blocks;
  - a worker row is **one line** - name, the phase dimmed beside it, the percentage on the right -
    with the bar under it. The elapsed time that repeated on every row is gone, and the full phase
    text stays in the log;
  - the phase no longer appears **twice**: while worker rows are shown they carry it, so the separate
    phase line is left empty (it is still used for a single-file run, where there are no rows);
  - the build number moved out of the progress line into the badge beside the engine version
    (`bundled 0.5.1 - v1.1.0.44`), where it belongs;
  - the status block lost its duplicate speech-cache line and its monospace wall-of-text look; the
    loaded plugin path is now one small dimmed line, since it only matters when something is wrong.

  The detail-view dialog uses the same 3px bars, so both places look alike.

## [1.1.0.43]

### Added
- **"Sync all episodes" in the item menu of a series or a season.** The detail-view side used to
  offer a single subtitle of a single video and deliberately hid itself for containers, so a series
  or a season could only be queued from the main SubSync page. The menu item now covers both:

  - **Scope** - the whole series or one season, with the episode count next to each choice.
  - **Smart language selection** - only the languages that actually exist in that scope, each with
    its episode and track count ("Swedish - 24 episodes, 26 tracks"), because the list is built from
    the bulk subtitle endpoint rather than guessed.
  - **The same batch queue as the main page** - so the configured mode applies: a parallel setting
    runs the episodes in parallel here too, shown live as `12/40 - 8/8 running - <episode>`.

  It appears wherever the item menu does, which is several different paths: the detail page's more
  button, the menu on a card or row in a library grid, a home row, an episode list, or a "see all"
  list. The item id comes from the card whose menu was opened and only falls back to the detail
  page's id from the URL.

- The single-item dialog was rebuilt on the same layout, so a movie and an episode now show the same
  compact rows with the track's language, whether it is an external file, and whether it has been
  synced before.

## [1.1.0.42]

### Changed
- **The extraction switch is gone: the container index is always used.** The Settings page carried a
  checkbox ("Read embedded subtitles through the container's own index") wired to a property the
  configuration model never had, so it remembered nothing and controlled nothing, while the legacy
  dashboard page held the real switch. Both are removed - embedded subtitles are always read through
  the container's own index, falling back to ffmpeg for anything the index cannot describe, with the
  reason logged.

### Fixed
- **A sync that changes nothing no longer writes a file.** ffsubsync produces an output even when the
  timings come out identical, which left a `.SYNCED` sidecar with the same timing as its source
  beside the original (reported from real use after syncing embedded tracks). The change is measured
  before anything is written and the job finishes as *already in sync - nothing written*.

- **The measured offset was only accurate to the second.** The cue parser read the milliseconds out
  of every timestamp and then ignored them, so a real 400 ms shift was reported as `0 ms offset` in
  the History list and framerate ratios came out distorted (1.3x for a 0.5% correction). This also
  mattered for the rule above: with the old parser a genuine sub-second correction would have been
  discarded as "no change". Fractional seconds are parsed now, and the checks assert that a 400 ms
  shift measures as 400 ms while a zero-median framerate correction is still treated as a change.

## [1.1.0.41]

### Changed
- **Jellyfin is only asked to re-read what changed.** After a sync the plugin reports the one
  folder that changed (never a library-wide scan) and refreshes the item **once per item** instead
  of once per subtitle track - a ten-track episode used to re-probe the same media file ten times.
  The folder report is never skipped: it is what makes Jellyfin discover the new file, and Jellyfin
  coalesces repeats itself. The item refresh runs again after a minute, so syncing the same episode
  later still shows up.

### Fixed
- **A library hiccup can no longer fail a finished job.** Reporting the change and refreshing the
  item sat inside the job's error handler, so an exception there marked the job FAILED and rolled
  back a subtitle that had been written correctly. Both calls are best-effort now: they log a
  warning and leave the result alone.

- **A completed job states the size of what it wrote**, and says so when the file is not on disk
  afterwards (a share that dropped it) instead of reporting success and telling the library about a
  file that is not there.

## [1.1.0.40]

### Fixed
- **The parallel-worker setting now saves.** The Settings field and the worker-rows container
  shared the id `ss-workers`, so `getElementById` returned the container: the field was never
  filled from the saved settings, and every save read `undefined` off a `<div>` and stored the
  fallback of 4, whatever was typed. The field has its own id now, and the save reads back what
  the server stored, saying so when the two differ ("Saved, but the server stored 4 while 8 was
  asked for") - a save that quietly stores something else is no longer possible. The checks now
  assert that every element id is unique and every settings field resolves to exactly one element.

- **`Could not start batch: ... The value 'series' is not valid.`** The scope dropdown started
  with a placeholder `<option value="series">` until the season list arrived. Clicking Sync inside
  that window sent the literal word `series` as the item id, which Jellyfin rejects - and it looked
  random because a page refresh usually loaded the seasons first. The placeholder now carries no
  value, and a scope that is not a real id falls back to the whole series, with a line in the log
  saying that is what happened.

- **The progress line no longer reads "3/1 workers".** The width shown is the one the jobs that
  are actually running use, instead of the mode of the batch sitting at the top of the queue.

## [1.1.0.39]

### Fixed
- **A library the plugin cannot write to is now reported once, with the folder named.** Reported
  from real use as `Access to the path '/media/Serier/…-SYNCED.heb.srt' is denied` for every
  subtitle in that library. The folder is the problem (a read-only mount, or a share owned by
  another user), not the subtitle, so the check happens before any work:

  ```
  Cannot write the synced subtitle to '/media/Serier/Black Mirror/…': the Jellyfin user has no
  write permission there (the folder may also be mounted read-only). Nothing was changed and the
  original subtitle is untouched. Fix the folder's permissions for the Jellyfin user (or how the
  library is mounted) and run again.
  ```

  It applies to all three write paths (copied sidecar, embedded sidecar, replaced original), and
  the OS message is included when there is one — `Read-only file system` and `Permission denied`
  need different fixes. Nothing is left behind by the check.

## [1.1.0.38]

### Fixed
- **Synced sidecars are named so Jellyfin can associate them with their video.** A sidecar must
  start with the exact media filename followed by DOT-separated fields (Jellyfin's media naming
  rule: `Film.mkv` → `Film.en.sdh.srt`). The marker was glued on with a hyphen
  (`...-SYNCED.dan.srt`), which left Jellyfin unable to attach the file to the episode, so synced
  subtitles never appeared. Now:

  - embedded track → `Quicksand - S01E01 - Maja WEBDL-1080p.SYNCED.dan.srt`
  - copied external → `…stem.sv.SYNCED.srt`

  Files already written with the old hyphenated name keep that name; they can be renamed by hand
  or re-synced, and the plugin will not touch them on its own.

- **Settings now apply without restarting Jellyfin — all of them.** The plugin read the
  configuration copy loaded when it was constructed, so a saved change could reach the settings
  file and never reach the running plugin: the worker count stayed at its old value and "replace
  the original" kept writing copies. Every settings read now goes through `SettingsSource`, which
  re-reads the settings file whenever its timestamp changes, with the plugin's own copy as the
  fallback when the file cannot be read.

### Note
- For an **embedded** track, "replace the original" and "save a copy" are the same thing by
  design: the original lives inside the video, which is never modified, so a new external sidecar
  is always written. The two modes differ only for external subtitle files.

## [1.1.0.37]

### Added
- **The settings page states the worker value in force against the value configured**, plus where
  the plugin was loaded from and which settings file it reads:

  ```
  Workers: 4 in use (setting 8)
  Loaded from: /config/data/plugins/SubSync_1.1.0.37/Jellyfin.Plugin.SubSync.dll  ·  /config/plugins/SubSync.xml
  ```

  A setting that appears to be ignored has exactly two explanations — the running copy is not the
  one being edited, or the value never reached the settings file — and both are now visible
  without reading any code. The startup log carries the same facts
  (`SubSync <version> loaded from <assembly>; settings file <path>; parallel workers setting N`),
  so a second loaded copy shows up as well.

### Changed
- **The language box says LOADING inside itself** (same box, same position, text replaced, restored
  when the lists are in) instead of drawing a second layer over the existing text.

## [1.1.0.36]

### Added
- **The worker setting is now observable.** A run reported `4/4 workers` with 8 configured, and
  every path that reads the setting looked correct — so the value in force is now stated instead
  of inferred:
  - the log gets `SubSync worker limit is N (configured: M, ceiling: 64)` whenever the limit
    changes;
  - the run line shows `... · setting is M` whenever the configured value disagrees with the limit
    actually being used.

  If the two ever disagree again, one line says so.

### Changed
- **The language box keeps its options while the lists are read.** It used to replace its contents
  with a `LOADING` entry, which made the control look like something else. Now the box stays as it
  is (disabled) and the word and the real count appear *inside* it — `LOADING 45/280`.

## [1.1.0.35]

### Fixed
- **A batch can now use its full width on files that carry many subtitles.** Two causes, both
  found from a run reporting `2/4 workers` on ten-track episodes:
  - the scheduler's candidate scan stopped after `limit x 4` jobs — with ten subtitles per episode
    that covered **two** media files, so a four-worker batch could only ever be two wide;
  - a file's later subtitles were refused a slot by an extra "this job is heavy" veto, which
    overrode the sharing policy that is supposed to decide exactly that.

  Now the scan is wide enough to *find* a wave (still bounded, so one pass cannot stall on a huge
  queue), and the sharing policy is the only thing that decides whether two subtitles of one file
  may run together: an audio analysis that is stored, or a reference subtitle that has been
  extracted, both allow it.

- **The extracted reference subtitle is written atomically** (temporary file, then moved into
  place), so two subtitles of the same file extracting it at once can never leave a half-written
  file for the other to read.

Checks: 132, ALL PASS — including "10-track episodes still fill four workers" (which failed before
this change), "one episode's subtitles run four wide once its reference is cached", and the two
checks that had encoded the veto.

## [1.1.0.34]

### Fixed
- **A sibling-subtitle reference no longer makes ffsubsync read the whole episode.** For an
  embedded track the plugin points ffsubsync at the video and selects another subtitle stream as
  the reference (`--reference-stream s:N`). ffsubsync then demuxes the **entire file** to pull
  that stream out — measured on an 8.2 GB episode: **12.5 s, 8218 MB read**, and it happened again
  for every subtitle of that file. Ten tracks meant ten full reads of the episode, which is what
  looked like "it analyses the same episode each sub" and like a stall.

  The reference subtitle is now extracted **once** by the plugin's own container-index reader
  (measured: **29.5 ms, 0.1 MB**), cached next to the speech cache, and handed to ffsubsync as a
  small SRT: **0.6 s, 6.5 MB** per subtitle. Later subtitles of the same file reuse the cached
  copy — which is what the progress line reports as `Syncing (from cache)`.

  Output is unchanged: the same subtitle extracted by our reader and by ffmpeg produce identical
  results, and a sync against the extracted file is byte-identical to a sync against the stream
  (both verified on the 8.2 GB file). If the reference cannot be extracted from the container
  index, the previous behaviour is kept so a sync never fails over this.

## [1.1.0.33]

### Fixed
- **The sync phase now names what ffsubsync actually does.** Three different situations shared
  one label, so a cheap 0.6 s subtitle comparison looked like a fresh audio analysis on every
  subtitle of the same file:
  - `Syncing (from cache)` — the stored speech analysis is being reused (this was "reusing the
    audio analysis", now stated in the terms you asked for),
  - `Syncing (analysing the audio)` — the audio is actually being analysed,
  - `Syncing (using another subtitle track)` — a sibling subtitle track is the reference, so no
    audio work happens at all (this path previously kept the placeholder "Analyzing speech" set
    before the branch, which is what made it look like repeated analysis).

  Deciding the label moved into one testable helper, with checks asserting that no label claims
  speech analysis when none happens.

### Changed
- **The language dropdown says `LOADING <done>/<total>` while it reads subtitle lists**, and is
  disabled until the list is in. Showing "All languages" before anything had been read claimed a
  completeness the list did not have yet.

## [1.1.0.32]

### Reverted
- **Thread pinning removed.** 1.1.0.31 forced ffsubsync's numeric libraries to one thread per
  worker. The measurement was real (4 threads per worker unpinned, 1 pinned, same work), but the
  conclusion was mine, not a reported fault — four threads across four workers is a legitimate
  choice, and the CPU spike that prompted the look was a hung benchmark process on the server, not
  the plugin. The plugin runs ffsubsync exactly as it did before 1.1.0.31; no environment
  variables are set, and the built assembly contains no trace of the code.

### Kept
- `<AssemblyVersion>` and `<FileVersion>` expanding from `<Version>`. Not a behaviour change:
  they had been stuck at 1.1.0.19, so the version line added in 1.1.0.29 would have reported the
  wrong build. The interface now shows the truth.

## [1.1.0.31]

### Fixed
- **One worker no longer opens four threads.** Measured on a real 1h52m remux: the bundled
  ffsubsync's numeric libraries peak at 4 threads per process; pinned, they peak at 1 while doing
  the same work at the same share of a core. Worker count and thread count multiply, so four
  workers on a four-core box were demanding up to sixteen threads — a load average above the core
  count with no visible cause. `OMP_NUM_THREADS`, `OPENBLAS_NUM_THREADS`, `MKL_NUM_THREADS`,
  `NUMEXPR_NUM_THREADS` and `VECLIB_MAXIMUM_THREADS` are set to 1 for the ffsubsync process (its
  children inherit them). The worker setting remains the only thing that decides parallelism.
- **The version no longer contradicts itself.** The catalog served 1.1.0.30 while the assembly
  still said 1.1.0.19, because `<AssemblyVersion>`/`<FileVersion>` had not moved since that
  release — and the version shown in the interface is read from the assembly. Both now expand from
  `<Version>`; the built DLL contains exactly one version string, `1.1.0.31`.

## [1.1.0.30]

### Fixed
- **The end-of-extraction summary carries the same numbers as the live lines.** It printed
  `kernel -1 bytes in -1 calls` because those fields were only filled inside the progress
  callback. A log line that says "unknown" where a number belongs invites exactly the doubt this
  telemetry exists to remove. It now reads, for example:
  `seekhead-cues: 326 clusters, 326 blocks, 1.3 MB in 331 reads, 22.8 ms | 0.0 ms/read, 14310
  blocks/s, kernel 1335458 bytes in 329 calls` — two independent counters that must agree.

### Verified
Both plausible cluster layouts cost the same, so the reader is not reading across clusters:
a block 32 bytes into a 5 MB cluster and a block at the end of it both give ~330-390 reads and
1.3 MB over 326 cues.

## [1.1.0.29]

### Fixed
- **Extraction progress is now honest and checkable.** The line printed counters that were only
  filled in after extraction finished, so it read `0.0 MB, 0 reads` the whole time — a claim about
  cost that could not be checked while it was being paid. Counters are live, and bytes/reads come
  from the kernel (`/proc/self/io`) as well as the reader, so the two must agree.
- **The line reports latency**: `reading subtitle 128/326 · 1.3 MB, 371 reads · 0.1 ms/read · 26
  cues/s`. On storage where a read costs 12 ms, 330 reads is 4 s; where it costs 50 ms, it is
  16 s — which is what a shared disk head does to several workers at once.
- **The extraction bar moves.** The Matroska path only ever set the phase text, leaving every
  worker frozen at 5% — four independent workers looked synchronised because the number under
  them did not change, even though their cue counts differed. The bar now follows the cue
  fraction (5% → 20%).
- **The running version is shown** in the run line, so which build is in use is never a guess.

### Measured
A fixture shaped like the reported episodes (326 subtitle cue points, 5 MB clusters, 1.7 GB
apparent; no library file involved):

  read calls  342–371 (kernel-verified)   ≈1.1 reads per cue
  bytes read  1.3–1.4 MB                  ≈4 KB per cue, 0.08% of the file
  output      326 cues, byte-identical to ffmpeg's extraction

Embedded subtitles sit in scattered clusters; one small read each is the floor, so extraction
time is that floor times storage read latency (times contention when several workers share one
disk). The line now states the multiplier instead of leaving it to interpretation.

## [1.1.0.28]

### Fixed
- **Any worker count is honoured.** The setting was capped or rewritten in five places: the
  service clamped it at 8, both settings pages carried `max="8"` and `Math.min(8, ...)` on save,
  and the batch view reported a hard 16 — asking for 32 silently produced 8. The ceiling is now
  one documented constant (`MaxParallelWorkers = 64`), stated in the settings text, and every
  value in range is used exactly as written. The batch view advertises the ceiling so the pages
  clamp against the same number the server uses, and the worker list scrolls so a 32-row batch
  stays readable.

## [1.1.0.27]

### Fixed
- **Workers no longer wait for each other.** The pump ran jobs in groups and awaited the whole
  group, so when three of four finished their work, those three slots stayed idle until the
  slowest job finished — and then every worker started its next step in the same instant, which
  is also the worst moment to hit the disk. With extraction now fast, that waste is what you see:
  three done, one bar left, and then four extractions starting together.

  A job now occupies a **slot**: as many jobs start as there are free slots, and a job that
  finishes wakes the scheduler immediately so its slot is refilled with the next queued job. No
  barrier, no lockstep. A cold start still fills every slot at once, so starts within one
  dispatch are staggered by 400 ms so several workers do not begin reading in the same instant.

  What is preserved: a subtitle of a media file that is already being read waits for that run
  (a second worker would race to build the same stored audio analysis) — it simply no longer
  holds anyone else back; and an idle volume is preferred when choosing the next job, without
  ever limiting how many run.

## [1.1.0.26]

### Added
- **Visible batch width.** The batch view now reports the effective worker limit and the run
  line shows `2/4 workers` (running / limit) whenever a parallel batch still has work left, and
  every wave logs its own width: `Wave: starting 2 job(s) (worker limit 4, mode ultimate, 37
  still queued)`. Asked "why did a 20-episode series only run two at a time?" the scheduler had
  no answer to give; now the setting and the wave size are both stated.

  The selector itself was verified against that exact shape: 20 episodes, two subtitles each,
  one disk — it selects four jobs, and eight when eight workers are configured.

## [1.1.0.25]

### Fixed
- **Extraction now uses `CueRelativePosition`, which is why it was still slow.** Real remuxes
  record, for every cue point, the exact byte offset of the referenced block inside its
  cluster — the remux here has it on 1873 of 1873 cue points, written by both ffmpeg and
  mkvmerge. The reader ignored it and walked the cluster's blocks instead, and a remux cluster
  holds ~150 of them (mostly audio frames): an episode with 344 subtitle cue points meant tens
  of thousands of small reads. Now an index entry means *one* small read.

  Measured, same files, same output:

  | case | before | after |
  | --- | --- | --- |
  | real 8.2 GB remux, 4 subtitles | 127 reads, 7.0 MB | **28 reads, 0.1 MB, 27 ms** |
  | 1,400 subtitle cue points | 1,421 reads, 91.8 MB | **1,413 reads, 5.8 MB** |

  Output is byte-identical to ffmpeg's own extraction of the same track. When a file has no
  block offsets (or holds a cluster in a shape that does not match), the cluster walk still
  runs, so nothing regresses — the harness asserts that the indexed path reads less than the
  walk on identical fixtures.

- **Fixed a time offset the indexed path introduced**: CueTime is the seek point's timestamp,
  which for the referenced block is already its own time, so using it as the base and adding
  the block's relative timecode double-counted it (+51 ms and +459 ms against ffmpeg). The
  cluster's Timecode element is used as the base instead — the same base the walking path
  uses — which is muxer independent.

### Changed
- Extraction progress reports its cost while it works: `reading subtitle 80/344 (1.2 MB, 96
  reads)` instead of a bare cluster counter, so a slow read can be told apart from a slow disk.

## [1.1.0.24]

### Fixed
- **Kill now kills.** The sync's ffsubsync run was started with `CancellationToken.None`, so
  the per-job token that Cancel/Kill fires never reached it: the process kept running after
  the button was pressed, and so did the ffmpeg it spawns internally. Every child process
  (ffsubsync, both ffmpeg paths) now gets the job's token, all of them are registered while
  they run, Kill terminates whole process trees directly instead of relying on a token being
  noticed, and the log reports what was killed and whether anything survived. A killed job is
  recorded as *cancelled*, not failed. The in-process Matroska extraction honours the token
  too, so a slow read can be interrupted instead of holding a worker.

- **Reading a cue cluster no longer costs one disk round trip per block.** A real remux holds
  ~150 blocks in a cluster (mostly audio frames), and the reader has to look at each one to
  find the subtitle block — with unbuffered reads that was ~150 round trips per cluster, and
  an episode with 315 subtitle cue clusters meant roughly 46,000 of them. That is the
  "reading cluster 32/315" crawl. Reads are now windowed: a 4 KB window while walking cluster
  headers, and a window sized to the cluster (up to 512 KB) while enumerating the blocks of a
  cluster the cue index pointed at. Measured on a real 8.2 GB Blu-ray remux: **540 → 127 read
  calls**, 29.6 ms, identical 4 cues.

### Changed
- **The audio analysis is always kept.** It was only cached in `fast`/`ultimate` mode, so a
  single-subtitle sync threw the result away and every later run (or the next subtitle of
  that file) analysed the audio again. The cache key is per *media file* (path + VAD method +
  engine build), never per title, so a series gets one entry per episode and nothing is keyed
  by name. Since the analysis happens anyway, keeping it costs one small file and makes
  re-runs and extra subtitles skip the audio pass. The UI no longer narrates caching — the
  phase simply says *analysing the audio* or *reusing the audio analysis*.

- **Run line simplified.** While the batch is assembled it says `Loading…` instead of a
  running commentary of file and track counts. While it runs it shows only what is meaningful:
  a strategy word (`parallel`, `reusing the audio analysis`) when there is one to state, the
  worker count only when more than one worker is active, and a single position counter
  (`episode 6/160`, or `task 4/12` when the batch covers one episode). Sequential runs no
  longer label themselves "single", and episode/task counters are no longer repeated.

### Audited
- The library browser and series sync use the same service code as every other entry point,
  so the settings apply there too: the language filter and image-track exclusion (enforced
  server-side when tracks are listed *and* re-checked when a job runs), the sync strategy and
  worker count, copy vs replace, golden-section search, VAD method, ffmpeg/ffsubsync paths,
  encoding, offset limits, and the indexed-extraction settings. The browser sends no mode of
  its own — it inherits the configured one, including the automatic strategy.

## [1.1.0.23]

### Fixed
- **Several subtitles of one movie now sync in parallel, not one after another.** The
  automatic strategy picked *fast* (reuse only) whenever a batch covered a single media file,
  and *fast* is not a parallel mode — so the first subtitle analysed the audio and every
  other one then queued up behind it, each waiting its turn even though its work was already
  cheap. That case now resolves to *ultimate*: the first subtitle builds the speech analysis,
  and the remaining ones align in parallel against that cached signal, up to the configured
  worker count.

  The file-sharing rule still applies and is what keeps this safe: a second subtitle of the
  same file only joins a wave once that file's speech analysis is cached, so the analysis is
  built exactly once and no two workers race to build it.

  Explicit modes are unchanged — *Fast* still runs sequentially with reuse if you select it
  in Settings for troubleshooting.

## [1.1.0.22]

### Fixed
- **Embedded Matroska extraction is now genuinely index-based.** The previous reader located
  subtitles with hundreds of thousands of tiny reads and then read the *video payload* of
  every block it looked at, so it cost a large share of the file instead of a few
  kilobytes — and when the cue index had no entries for the subtitle track, it gave up and
  let ffmpeg demux the whole file, which is the "extraction takes longer than the sync" case
  on a remux.

  Measured on a 63 GB Blu-ray-shaped remux (12,000 clusters, cue index at the end of the
  file), producing the same 40 subtitle cues:

  | case | before | after |
  | --- | --- | --- |
  | subtitle cue points present | 612 ms, 996 MB read, 12,214 reads | **28 ms, 0.2 MB, 505 reads** |
  | no cue points for the track | failed → ffmpeg reads 63 GB | **95 ms, 1.8 MB, 108,138 reads** |
  | no Cues element at all | failed → ffmpeg reads 63 GB | **113 ms, 1.7 MB, 120,134 reads** |

  What changed in `MkvSubtitleExtractor`:
  - the SeekHead is used to jump to Tracks and Cues instead of walking every cluster;
  - the cue index is read in one bulk pass and parsed from memory;
  - block *headers* are read first and payloads only for the wanted track, so other tracks'
    data is skipped rather than read;
  - when the index has no entries for the track (or no Cues element), the plugin now scans
    cluster metadata only — headers read, payloads skipped by seeking — instead of falling
    back to ffmpeg, which read the entire file;
  - reads are unbuffered, so a seek costs the bytes it asks for instead of a 64 KB refill;
  - truncated or malformed elements stop the scan gracefully instead of failing the
    extraction.

- Extraction now reports what it cost in the log (`method, clusters, blocks, MB, read
  calls, timings`), and the job phase tracks the work (`Extracting subtitle: reading cluster
  12/40 from the cue index`, `scanning clusters (400 read, 0.4 MB, 7 subtitles found)`), so a
  slow extraction is visible instead of frozen at 5%.

## [1.1.0.21]

### Changed
- Parallel waves now **spread across storage volumes as a preference**: the scheduler takes
  one file per volume first, then fills the remaining worker slots with whatever is left,
  same disk or not. A batch that spans several disks runs one file from each instead of
  hammering one; a batch that lives on a single disk still runs at the full worker count,
  because nothing is blocked. Volumes never cap a wave — they only decide which job goes in
  first. The rule that several subtitles of the same media file never start before that
  file's audio analysis is cached is unchanged.

## [1.1.0.20]

### Removed
- The **per-volume heavy-read limit** is gone, along with its `HeavyReadsPerVolume` setting.
  Waves are bounded by `Parallel workers` and nothing else, so parallel work now runs at the
  width you ask for — four workers means four tasks, whether or not they sit on one disk.
  Storage scheduling is left to the OS, which sees the real device queue. The one remaining
  rule is correctness, not throttling: two subtitles of the *same* media file never run
  before that file's speech analysis is cached, so they cannot race to build it.


