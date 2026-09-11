# Side issues found while fixing extraction (for a later session)

Things I hit, proved, and deliberately did **not** fix in the extraction session. Each one has the
evidence that found it, so it can be picked up cold.

## 1. The scheduler pump sleeps for ~45 s with a full queue and free workers

**Symptom.** A real run on 2.0.7.0: 240 jobs queued, 8 episodes; the plugin goes completely silent —
no dispatch, no ffsubsync, no extraction — for 46 and then 83 seconds, then bursts of 4 jobs at once.

```
16:28:31 - 16:28:40   ~10 jobs, one per second
16:28:40 - 16:29:26   NOTHING for 46 s   (queued 225, running 0, limit 4)
16:29:26 - 16:29:30   4 ffsubsync runs start together
```

**Why it matters.** This, not extraction, is what makes a batch feel like "2 minutes per episode".
Each ffsubsync run is ~1 s, so 240 jobs should take ~1-2 minutes; the run took far longer.

**Hypothesis.** `SubSyncService`'s pump does `await _wakePump.WaitAsync()` with no timeout when
`PlanStart` returns an empty list. `PlanStart` refuses to start a job for a file whose reference /
audio analysis is not ready, so when the head of the queue is such a file the pump sleeps until some
other event signals it (the next enqueue wakes it — hence burst → silence → burst). A bounded wait
(`WaitAsync(TimeSpan.FromSeconds(1))`) would re-plan every second and remove the silence. The phase
label shown in the UI during the silence is "Extracting N subtitles … in one pass", which is why
extraction got blamed for it.

**Verify before/after with:** number of ffsubsync exits per minute in the plugin log (should rise from
~10/min towards the limit × 60 / seconds-per-job).

## 2. Per-track extraction with immediate sync (the user's design)

Each job should extract only its own subtitle track and sync it immediately, instead of one pass
serving every queued language of the file while the file's other jobs wait. This is what removes the
"Extracting 30 subtitles in one pass" phase entirely and lets four languages of one episode run at
once. The reference has to stop being a shared extracted file first (see 3) or jobs still wait.

Measured trade-off to settle with numbers: per-track extraction does more total reads (56 tracks ×
~750 block reads vs one pass of ~1,500 reads) but no track waits for another. On a NAS where a read
costs ~3 ms, the currency is read *operations*, so this needs measuring, not assuming.

## 3. The plugin extracts a reference subtitle that ffsubsync could read itself

Their log shows both sides being extracted by the plugin:

```
ffsubsync /config/cache/subsync/ref/<hash>/<hash>.ref.srt -i /config/cache/subsync/<job>/subtitle_9.srt ...
```

ffsubsync 0.5.1 (bundled) has:
- `--reference-stream s:N` / `--reftrack` — read one embedded subtitle track from the MKV as the
  reference, no plugin-side extraction;
- `--pgs-ref-stream` — same for image-based PGS tracks;
- `try_fit_using_embedded_subs()` / `_extract_embedded_subs_single_pass()` — it probes and extracts a
  video's embedded subtitle streams itself in one pass (that is its default VAD `subs_then_webrtc`);
- `--extract-subs-from-stream N` — extract a subtitle stream itself with its bundled ffmpeg.

Handing the reference to ffsubsync removes the shared resource every job of a file currently waits on
(see 1 and 2). Caveat: ffsubsync's own extraction is ffmpeg, i.e. the full-file read.

## 4. "Clear cache" does not clear Jellyfin's own subtitle cache

The plugin's button clears the audio analysis, the extracted subtitles and the run references. It does
not touch Jellyfin's own `transcodes`/subtitle cache under the server's cache dir, which the plugin
also writes into for its `subtitle_N.srt` working files. Worth deciding whether it should.

## 5. Job phase text stays on screen after the job stops being the bottleneck

`job.Phase` is set to "Extracting N subtitles from the Matroska index in one pass" at the start of an
extraction, and the UI shows it while the job waits for a worker slot or its file's gate. That is what
made "extraction" look like the 2-minute cost. Phases should be honest about waiting versus working.

## 6. Storage probe: 2.0.6 measured the page cache

2.0.6 read the same 16 KB offset three times to time the storage; the 2nd and 3rd reads come out of
RAM, so a share answering real reads in 11 ms was reported as 0.00 ms and the walker chose 4 KB
pieces (611 MB in 10,782 reads per pass, 42 s). Fixed in 2.0.7 by stepping forward at three distinct
offsets — but any future probe must read *distinct* positions and should probably repeat the whole
thing once to be sure the first read was not a cache miss.

## 7. `tracks=1/7` — tracks with no index entry at all still extract alone

Tracks with a cue entry but no block offset are now served by the shared pass (2.0.7). Tracks with no
index entry whatsoever cannot be located cheaply, so their jobs still run their own full pass. On this
file 6 of 30 wanted tracks took that path. Worth deciding: extract them by walking the cluster the
pass is already reading for other tracks (they share clusters), or accept the extra pass.

## 8. The plugin log prints a list object instead of its contents in places

`args=System.Collections.Generic.List`1[System.String]` appeared in older builds' ffsubsync lines. The
current build joins the list (`string.Join(' ', args)`) — check the other log lines for the same
mistake, since an unreadable argument line hides exactly the information needed when a run goes wrong.

## Extraction lane (added this session, verified on a real episode)

Measured on `Helikopterrånet S01E01` (2.38 GB, 50 subtitle tracks, real file pulled from the
user's server): 16 subtitle tracks of one episode go from queue to done in **4.9 s**, with no
duplicate reading per job and no scheduler silence. Jobs get their subtitle from the lane
(`method=reused` / `method=cache`), not by reading the file.

### Still outstanding: the reference track is extracted by a job, not by the lane

Three jobs still ran whole-file passes of ~590-870 MB each (2.4 GB total) in that run, and the
lane's own passes cost 944 MB. The lane now asks for the reference ordinal
(`ReferenceOrdinalsFor`, using Jellyfin's stream list), but the job's own pick is built from an
**ffprobe** list (`ParseProbeSubtitleCodecs`) - when the two disagree the job extracts the
reference itself. Two tracks to pull:

1. Make the lane's reference choice identical to the job's: have the lane call the same picker
   with the same ffprobe-derived codec list (probe once per file, then reuse), or pass the lane's
   choice into the job so there is exactly one opinion per file.
2. `/SubSync/Sync` `Failed` with `ffsubsync exited with code 1. Last output: ERROR unable to read
   reference ...` - the reference path given to ffsubsync did not exist at that moment, i.e. the
   reference file was discarded (or never written) while a job still pointed at it. Seen once in
   16 in the last run, twice in 12 earlier. The failure message is now honest about it; the
   ordering bug behind it is still there.

Reproduce both with 12-16 tracks of one episode queued at once; the lane's own log line
(`extract lane: <file> -> N/M subtitle(s), MB, reads, ms`) separates lane cost from job cost.
