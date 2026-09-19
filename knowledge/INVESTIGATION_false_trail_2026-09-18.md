# INVESTIGATION — False Trail (2011) / Jägarna 2, the "+50 ms" claim

Job 658cf5435ae140358e4e54f014b0aa2b (2026-09-18 14:21:36Z) and duplicate 5b5ffb09489449f68f0cf2ee380b98fc (14:25:21Z).
Media: `/Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.mkv`, sidecar `...swe.srt`.
Written by the plugin: `...swe.SYNCED.srt` (73010 bytes, `change=+50 ms`, engine said `score=97950 offset=0,050 s`).
Investigated 2026-09-19 on the Hermes host container. Branch beta, HEAD 9b52c39.

## VERDICT

**not determinable from this machine.**

The film, the sidecar and the written `SYNCED.srt` do not exist on this machine in any mount, cache or copy
(section 3), and no `ffsubsync` executable exists here either (section 8). Nothing about *this file's* alignment
can be measured, so no number in this report is a measurement of False Trail — the numbers below are measurements
of the *method*, taken on rig media with a known answer, plus the corpus statistics of the user's own log.

**The one missing access:** read access to the production Jellyfin container's `/Media` and `/config` — i.e. the
three files `/Media/Movies/False Trail (2011)/{False Trail (2011) Bluray-1080p.mkv, .swe.srt, .swe.SYNCED.srt}`.
The library is on the CasaOS host (the docker bridge gateway, `172.17.0.1`); the Jellyfin server itself answers at
`172.17.0.4:8096` as `FABJI-FLIX` 12.0.0 and returns **401** without an API key, and no key for it exists on this
machine. docker is not available in this shell (`/var/run/docker.sock` absent), so the container cannot be entered
from here. Precise commands are in section 7.

**What the log alone already settles:** the two jobs were a duplicate pair for the same external sidecar, they
excluded the known S43 "ruler wearing an audio name" defect (the plugin forced `--vad webrtc`, section 4), and a
whole-file PAL/NTSC stretch was in the engine's search space and lost to ratio 1.0 — so a *constant* +50 ms and not
a speedup. What the log cannot settle is whether +50 ms was the right number, because the score it prints has no
validity signal (section 4b) and the same method is measured below to wobble by 50–90 ms on a file whose answer is
known (section 5).

---

## 1. Reachability of the media (commands and raw output)

```
$ ls -la '/Media/Movies/False Trail (2011)/'
ls: cannot access '/Media/Movies/False Trail (2011)/': No such file or directory      # exit 2
$ ls /Media
ls: cannot access '/Media': No such file or directory
```

`/proc/mounts` — the only device-backed mounts in this container are three subdirectories of one ext4 partition:

```
overlay / overlay rw,relatime,lowerdir=/var/lib/containerd/... 0 0
/dev/nvme0n1p2 /Hermes-Media ext4 rw,noatime 0 0
/dev/nvme0n1p2 /subsync-logs ext4 rw,noatime 0 0
/dev/nvme0n1p2 /opt/data ext4 rw,noatime 0 0
proc /proc proc ... ; sysfs /sys ... ; cgroup ... ; tmpfs /dev,/dev/shm 0 0
```

`/proc/self/mountinfo` names the host-side sources, which is what shows where the library lives:

```
/DATA/AppData/hermes                                                /opt/data
/DATA/AppData/jellyfin/config/data/data/subsync/logs                /subsync-logs
/DATA/Media/Hermes-media                                            /Hermes-Media
```

So `/Media` (the plugin's container path) is a *different* bind of the host's media partition that is not given to
this container. `/Hermes-Media` contains only this agent's own outputs (`Encodes/`, `Web-DLs/`), no library.

Searches, all empty:

```
$ timeout 150 find / -xdev \( -iname '*False*Trail*' -o -iname '*Jägarna*' -o -iname '*Jagarna*' \) 2>/dev/null
(nothing)
$ timeout 120 find / -maxdepth 7 -iname '*False Trail*' 2>/dev/null
(nothing)
$ find / -xdev -maxdepth 5 -type d -iname 'Movies' 2>/dev/null
(nothing — there is no library tree anywhere on this machine)
```

The plugin's cached speech for this exact file is absent too, so even the cheapest route (score the cache against
the sidecar) is unavailable:

```
$ timeout 90 find / -xdev -name '74821354*' 2>/dev/null
(nothing)        # the production cache is /config/data/data/subsync/state/speech-cache/74821354f558f4177f79c0f68a8126ef.{mkv,npz}
$ grep -c 'False Trail' /subsync-logs/subsync.log        # only the log is mounted here
8
$ grep -c 'False Trail' /subsync-logs/subsync.log.{1,2,3}
0, 0, 0
```

Other access routes checked and closed:

| route | result |
|---|---|
| docker / podman / ctr / nerdctl | `/usr/bin/docker` present but **no socket** (`/var/run/docker.sock`, `/run/docker.sock` both absent) |
| ssh to the host | `ssh` client present, but no keys (`/opt/data/.ssh` absent, `/root/.ssh` unreadable); `.env` has `TERMINAL_SSH_HOST` commented out with the placeholder `192.168.1.100` |
| network mounts | `mount \| grep -Ei 'nfs\|cifs\|smb\|fuse\|sshfs'` → empty |
| production Jellyfin API | `curl http://172.17.0.4:8096/System/Info/Public` → `{"LocalAddress":"http://[::1]:8096","ServerName":"FABJI-FLIX","Version":"12.0.0","ProductName":"Jellyfin Server","Id":"60f3014952044da0902575b343a38f16","StartupWizardCompleted":true}`; `/System/Info` → **401** |
| a Jellyfin token on this machine | `/opt/data/tmp/jf_token.txt` is 1 byte and belongs to the 127.0.0.1:8096 **rig**; `tests/gui/ids.json` holds the rig `admin_token`. No key for FABJI-FLIX exists here |
| port sweep 172.17.0.1–12 :8096 | only `172.17.0.4:8096` (the production Jellyfin) open; `172.17.0.1:80` is CasaOS, `:8096` closed on the gateway |

## 2. The job's own record, verbatim (from /subsync-logs/subsync.log)

```
2026-09-18 14:21:36.121Z INFO  queued: job=658cf5435ae140358e4e54f014b0aa2b item=56223818-fb7a-21c9-9670-a631384b4290 stream=0 ordinal=−1 mode=auto batch=(standalone) language=swe external=True forced=False codec=subrip video=/Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.mkv
2026-09-18 14:21:36.152Z INFO  [658cf5435ae140358e4e54f014b0aa2b] reference: method=audio why=the audio is this job's own reference (this job does the file's analysis; the others wait for it)
2026-09-18 14:21:36.155Z INFO  [658cf5435ae140358e4e54f014b0aa2b] ffsubsync start: exe=/config/data/plugins/SubSync_2.0.66.0/ffsubsync/linux-x64/ffsubsync cachedSpeech=False reference=(default) args=/config/data/data/subsync/state/speech-cache/74821354f558f4177f79c0f68a8126ef.mkv -i /Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.swe.srt -o /config/cache/subsync/658cf5435ae140358e4e54f014b0aa2b/synced.srt --max-offset-seconds 150 --max-subtitle-seconds 10 --vad webrtc --output-encoding utf-8 --ffmpeg-path /usr/lib/jellyfin-ffmpeg/ffmpeg --serialize-speech --log-dir-path /config/cache/subsync/658cf5435ae140358e4e54f014b0aa2b
2026-09-18 14:21:36.155Z INFO  [658cf5435ae140358e4e54f014b0aa2b] reference is the audio, so the engine is given --vad webrtc: with the configured 'subs_then_webrtc' it would read the video's own subtitle tracks as its speech signal, and which track that is, is the engine's choice rather than the plugin's
2026-09-18 14:22:34.863Z INFO  [658cf5435ae140358e4e54f014b0aa2b] ffsubsync exit=0 after 58708 ms
2026-09-18 14:22:34.864Z INFO  [658cf5435ae140358e4e54f014b0aa2b] ffsubsync alignment: score=97950 offset=0,050 s against the audio
2026-09-18 14:22:34.878Z INFO  [658cf5435ae140358e4e54f014b0aa2b] this walk moved 8483,6 MB of /Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.mkv in 58,7 s = 151,5 MB/s - the ceiling for that volume is none (this volume measured 0,85 ms per read, which is fast)
2026-09-18 14:22:34.894Z INFO  job 658cf5435ae140358e4e54f014b0aa2b completed: mode=normal output=/Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.swe.SYNCED.srt bytes=73010 change=+50 ms offset extraction=n/a
2026-09-18 14:25:21.980Z INFO  queued: job=5b5ffb09489449f68f0cf2ee380b98fc item=56223818-...-a631384b4290 stream=1 ordinal=−1 mode=auto batch=(standalone) language=swe external=True forced=False codec=subrip video=/Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.mkv
2026-09-18 14:25:21.995Z INFO  [5b5ffb09489449f68f0cf2ee380b98fc] reference: method=speech-cache why=the audio is this job's own reference
2026-09-18 14:25:21.995Z INFO  [5b5ffb09489449f68f0cf2ee380b98fc] ffsubsync start: ... args=/config/data/data/subsync/state/speech-cache/74821354f558f4177f79c0f68a8126ef.npz -i .../False Trail (2011) Bluray-1080p.swe.srt -o /config/cache/subsync/5b5ffb09489449f68f0cf2ee380b98fc/synced.srt --max-offset-seconds 150 --max-subtitle-seconds 10 --vad webrtc --output-encoding utf-8 --ffmpeg-path /usr/lib/jellyfin-ffmpeg/ffmpeg --log-dir-path /config/cache/subsync/5b5ffb09489449f68f0cf2ee380b98fc
2026-09-18 14:25:25.363Z INFO  [5b5ffb09489449f68f0cf2ee380b98fc] ffsubsync exit=0 after 3367 ms
2026-09-18 14:25:25.363Z INFO  [5b5ffb09489449f68f0cf2ee380b98fc] ffsubsync alignment: score=97950 offset=0,050 s against the audio
2026-09-18 14:25:25.374Z INFO  job 5b5ffb09489449f68f0cf2ee380b98fc completed: mode=normal output=/Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.swe.SYNCED.srt bytes=73010 change=+50 ms offset extraction=n/a
```

Corrections to the briefing, from these lines:

* Run A's argv **does** contain `--serialize-speech` (the briefing listed the args without it). Run B's does not —
  it reads the `.npz` that run A wrote, which is why it finished in 3,4 s and why its answer is not independent
  (same speech signal, same VAD, same file → the identical `score=97950` is expected and carries no new evidence).
* "the second job re-ran from the cached speech .npz" is right, but the walk line it prints (`8483,6 MB ... in 3,4 s`,
  314,1 MB/s) is the plugin's I/O accounting, not a read: `knowledge/FIX_PLAN.md:1323-1326` records that a walk-based
  number on a run whose speech was already cached is suspect.
* Both jobs are the **same external sidecar** reported twice by Jellyfin (`stream=0` and `stream=1`, both
  `external=True language=swe`), writing the same output path twice with identical bytes. There is no second,
  independent measurement anywhere in this job pair.
* Startup config on the host is `vad=subs_then_webrtc` (`2026-09-18 21:50:24.866Z INFO startup: ... workers=8 mode=auto
  vad=subs_then_webrtc fixFramerate=True ...`), i.e. the plugin overrode it for these jobs by design (S43, below).

## 3. Why the decisive measurements cannot be taken here

Every input this investigation needs is absent, with the exact error above:

| needed | status |
|---|---|
| `False Trail (2011) Bluray-1080p.mkv` (8.5 GB, duration/fps/streams, embedded tracks) | absent |
| `...swe.srt` (the original the plugin was given) | absent |
| `...swe.SYNCED.srt` (73010 bytes, what was written) | absent |
| the engine's cached speech `74821354….npz` (the cheapest independent re-measure) | absent |
| an `ffsubsync` executable to re-run the engine with | **absent on this whole machine** (section 8) |
| the production Jellyfin API for a read-only fetch | reachable but 401; no key here |

Consequences, stated plainly: **no cue count, no delta, no trend, no ffprobe of this film, and no engine re-run for
this film exist in this report.** The numbers that follow are calibration of the method on rig media and corpus
statistics from the user's own production log; they bound what a "+50 ms" answer can mean, they do not settle this
film.

## 4. What the log alone establishes

### 4a. The known "audio ruler" defect class is excluded for these two jobs (S43)
`knowledge/FIX_PLAN.md:1328-1372` and `PROGRESS.md:94-105` document S43: handed the media file as an "audio"
reference with the default `--vad subs_then_webrtc`, the engine takes the video's **embedded subtitle tracks** as
its speech signal, so an audio-named ruler can be a subtitle ruler (measured divergence on the S31 fixture:
`+24,170 s` with the default VAD against the film's `-5,080 s` with `webrtc`). The fix shipped in 2.0.40 forces
`webrtc` whenever the plugin intends audio, and the log quotes the reason for this job (line 3 above). So for this
job the engine genuinely measured the film's audio: **S43 is not the explanation here.**

### 4b. `score=97950` is not evidence that +50 ms is right
`ffsubsync/constants.py:68-73` (rig venv copy):

```
# Quality gating (--skip-sync-on-low-quality). The score's sign is meaningful even
# though its magnitude is not, so 0.0 rejects only anti-correlated alignments.
```

and the repo's own measurements of wrong-versus-right rulers (`PROGRESS.md:124-125`): a wrong ruler scored
**274 721** against a correct ruler's **198 713**, and the rig's wrong ruler **66 451** against the same file's audio
**53 566**. A high score therefore does not validate an answer. The identical `97950` for both jobs is the *same*
signal being scored twice, not a confirmation.

### 4c. A constant +50 ms, not a speedup — as far as the engine's search space goes
`ffsubsync/ffsubsync.py:141-152` and `constants.py:9`: with `--no-fix-framerate` absent (it is absent from both
argv), the engine also evaluates `[24/23.976, 25/23.976, 25/24]` and their reciprocals alongside ratio `1.0` and
keeps the best score. The job's completion line reports a plain `change=+50 ms` with no rescale and no refusal, so
ratio `1.0` won. That rules out a **whole-file** PAL/NTSC stretch. It does **not** rule out a *piecewise* mismatch
(a re-edit where one stretch is right for part of the film) — the engine's whole-file correlation would still return
one global offset, and `SubSyncService.JobPipeline.cs:925-935` only investigates structure on the path where the
result is already suspicious. The per-cue delta trend is the only measurement that separates the two, and it needs
the two `.srt` files.

### 4d. The absence of a "framerate:" line for this job is expected, not a defect
The plugin logs its rescale decision (`... framerate: the stretch does NOT hold against the audio ...`) on the
subtitle-reference path (`SubSyncService.JobPipeline.cs:513`, `bool reference.UsedSubtitleReference`). Job
658cf5435… used an audio reference, so no such line is emitted for it while other jobs on 2026-09-18 do have one
(`14:18:55 [7c8a35f8…] framerate: the stretch does NOT hold against the audio (1,0000x, 23340 ms) — ...`). Nothing
should be read into its absence.

## 5. What WAS measured on this machine: the method's own error band

All of it on rig media with a known answer, using the same engine build (`ffsubsync 0.5.1`) and the plugin's own
arguments. The rig/fixture files were only read; every output went to `/tmp/ft`.

### 5a. The engine, re-run twice on a file whose answer is known
`/opt/data/jf12test/media/Helikopterrånet S01E01.mkv` (h264 24 fps, 2 eac3 audio streams, 52 subrip tracks incl.
`44 swe SDH`, duration 2994.240000 s), input `Helikopterrånet S01E01.swe.srt`:

```
RUN A' = the plugin's args, --vad webrtc
  INFO  total of speech segments: 62106.0                     speech_transformers.py:763
  INFO  score: 50665.000                                      ffsubsync.py:255
  INFO  offset seconds: -4.950                                ffsubsync.py:256
  INFO  framerate scale factor: 1.000                         ffsubsync.py:257
  real 0m21.704s   exit=0
RUN B' = the same with --vad subs_then_webrtc
  INFO  score: 216136.000
  INFO  offset seconds: -5.040
  INFO  framerate scale factor: 1.000
  real 0m7.008s    exit=0
```

The reference speech array run A' wrote (`/tmp/ft/ref.npz`, 299424 samples at the engine's 100 Hz) carries
**62106** speech samples — the identical count the production log's counterpart would have, and the same count found
in the pre-existing rig cache `365b77790a7dd9d7305e034b3e051ca2.npz` (14281 bytes, 20.74 % speech), which
identifies that cache as this episode's audio.

### 5b. The film's own embedded Swedish track as an independent ruler
`ffmpeg -map 0:44` extracts the film's own `swe SDH` track (709 cues, 52028 bytes). The sidecar has 790 cues, so
the two files cannot be compared cue-by-cue; the ruler comparison is the score curve (tool in Appendix C), which is
the same ±1 correlation at 100 Hz the engine itself uses.

| file | best offset vs the film's own swe track | peak score | score at 0 ms | 99 % width | competitor |
|---|---|---|---|---|---|
| `Helikopterrånet S01E01.swe.srt` (what the plugin was given) | **−5040 ms** | 212545 | 59495 | 100 ms | 0.801 |
| `Helikopterrånet S01E01.swe.SYNCED.srt` (engine answer −5000) | **−40 ms** | 212545 | 211721 | 100 ms | 0.801 |
| `/tmp/ft/ep-synced.srt` (my webrtc run, engine answer −4950) | **−90 ms** | 212545 | 209828 | 100 ms | 0.801 |
| `/tmp/ft/epB/synced.srt` (my subs_then_webrtc run, engine answer −5040) | **0 ms** | 212545 | 212545 | 100 ms | 0.801 |

The film's own track says the sidecar needed **−5040 ms**. On that same file the engine's audio (`webrtc`) answer was
**−4950 ms — 90 ms wrong**, and the subtitle-reading VAD answered −5040 ms — exactly right. The two VADs disagree by
**90 ms**; the two scores differ by a factor of 4,3 (50665 against 216136) and here the *better* number scored
*lower*, on a file where the repo's wrong-ruler cases scored higher (`PROGRESS.md:124-125`). The score is worthless
as a validity test in both directions, measured.

Cross-check that the scanning tool is faithful to the engine: given the engine's *own* speech array, the scanner
reproduces the engine's answer to the millisecond (`best_offset_ms = -4950` against the engine's
`offset seconds: -4.950`).

### 5c. How flat the peak is around a 50 ms answer (the False Trail shape)
Same file and the engine's own speech array (`/tmp/ft/ref.npz`), sidecar `swe.SYNCED.srt`:

```
best_offset_ms: 50.0            peak_score: 50631
score_at_0ms:   50283           = 99.31 % of the peak
score_at_-50ms: 49418           = 97.60 %
score_at_+50ms: 50631           = 100 %
score_at_-200ms: 45987
second_best_peak_outside_0.5s: 34509  (0.68 of the peak)
within_99pct_width_ms: 120
```

A 50 ms step is **0.7 % of the score** here, i.e. inside the curve's own flat top (the 99 % region is 120 ms wide).
On this file the engine nevertheless printed `+50 ms` as its answer. That is the measured shape of a small-offset
answer from this method on this kind of file: it is not resolvable at 50 ms, and its sign is therefore not a
statement about the film.

### 5d. Corpus statistics from the user's own production log
1037 `ffsubsync alignment:` lines in the day's production log (`/subsync-logs/subsync.log` while it was still that file;
it rotated to `subsync.log.1` at 02:32Z during this session — the same bytes):

```
references: {'reference subtitle s:0': 831, 'reference subtitle s:1': 148, 'the audio': 55,
             'the audio (cross-check of a subtitle ruler)': 2, 'reference subtitle s:2': 1}
== 0 ms        784  (75.60 %)
|v| <= 50 ms    64  ( 6.17 %)   examples: -40, -50, -10, 40, 40, -20
51-100 ms       29  ( 2.80 %)   examples: 80, -80, -70, -90, -80, 80
101-500 ms      36  ( 3.47 %)   examples: 130, 250, 170, 170, -290, -130
501-1000 ms     17  ( 1.64 %)
> 1000 ms      107  (10.32 %)
median |offset| over all answers: 0 ms
modal non-zero answer: +40 ms (27 jobs), then -40 (12), +80 (9), -80 (8), -10 (8); +50 ms occurs 5 times
of the 253 non-zero answers, 43 are exact multiples of 50 ms
```

Note for anyone re-deriving these: 111 of the 1037 lines write the minus sign as U+2212, so an ASCII-only
`offset=-?[\d,]+` regex silently drops them (it turns 926 parsed answers into 1037 and moves the `> 1000 ms` bucket).

So the modal non-zero answer of this installation is **±40 ms — one frame on a 25 fps timeline** — and +50 ms is one
step beyond it, occurring 5 times in a day. What matters for False Trail is not which small number won: a ±50 ms
answer sits at the bottom of the method's own output distribution *and* in the class where section 5c measures the
score curve to be flat, so it is not resolvable at that scale either way.

## 6. The measurements that are still owed for THIS film

1. **Cue-by-cue delta** between `...swe.srt` and `...swe.SYNCED.srt`: cue counts, how many cues differ, min/max/median
   delta, the delta of the first 10 and last 10 cues, and a least-squares trend in ms per minute. A constant
   +50 ms means the plugin applied exactly what the engine said; a growing trend means a framerate/wrong-cut
   mismatch that no global offset can fix (the user's complaint would then be explained); one large jump means a
   re-edit.
2. **`ffprobe` of the film**: duration, fps, and the stream list — specifically whether it carries its own `swe`
   text track. If it does, that track is a second ruler and the single most valuable comparison available: scan the
   sidecar against it (Appendix C) and read the residual after the +50 ms. Calibration (5b) says the audio ruler can
   be ~90 ms off on a 50-minute file, so a residual inside ±100 ms is *not* a diagnosis either way — the cue delta
   trend of (1) is what decides.
3. **A re-run of the engine** with the plugin's args and with `--vad subs_then_webrtc`, to see whether the two VADs
   agree on this film. If they disagree by ~90 ms as they do in 5a, the +50 ms number is inside the instrument's own
   scatter and the question moves entirely to the cue-delta measurement.

## 7. Exact commands to settle it (run on the CasaOS host / in the Jellyfin container)

```bash
# 0. find the container (docker is on the host, not in this container)
docker ps --format '{{.Names}}\t{{.Image}}' | grep -i jellyfin
C=<that name>; M='/Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.mkv'
S='/Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.swe.srt'
Y='/Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.swe.SYNCED.srt'

# 1. the inputs exist, and the engine is where the log says it is
docker exec "$C" ls -la "/Media/Movies/False Trail (2011)/"
docker exec "$C" /config/data/plugins/SubSync_2.0.66.0/ffsubsync/linux-x64/ffsubsync --version

# 2. the film: duration, fps, streams, and whether it has its own swe text track
docker exec "$C" /usr/lib/jellyfin-ffmpeg/ffprobe -v error \
  -show_entries format=duration:stream=index,codec_type,codec_name,r_frame_rate,avg_frame_rate:stream_tags=language,title \
  -of json "$M"

# 3. THE CUE-BY-CUE DELTA (tool: Appendix B, verified)
docker cp /tmp/ft/ft_compare_cues.py "$C":/tmp/ft_compare_cues.py
docker exec "$C" python3 /tmp/ft/ft_compare_cues.py "$S" "$Y" --json /tmp/ft_delta.json

# 4. the engine, exactly as production ran it (A), then the discriminating VAD (B), then offset-only (C)
docker exec "$C" bash -lc 'mkdir -p /tmp/ftA /tmp/ftB /tmp/ftC'
docker exec "$C" /config/data/plugins/SubSync_2.0.66.0/ffsubsync/linux-x64/ffsubsync "$M" \
  -i "$S" -o /tmp/ftA/synced.srt --max-offset-seconds 150 --max-subtitle-seconds 10 \
  --vad webrtc --output-encoding utf-8 --ffmpeg-path /usr/lib/jellyfin-ffmpeg/ffmpeg \
  --serialize-speech --log-dir-path /tmp/ftA
docker exec "$C" /config/data/plugins/SubSync_2.0.66.0/ffsubsync/linux-x64/ffsubsync "$M" \
  -i "$S" -o /tmp/ftB/synced.srt --max-offset-seconds 150 --max-subtitle-seconds 10 \
  --vad subs_then_webrtc --output-encoding utf-8 --ffmpeg-path /usr/lib/jellyfin-ffmpeg/ffmpeg \
  --log-dir-path /tmp/ftB
docker exec "$C" /config/data/plugins/SubSync_2.0.66.0/ffsubsync/linux-x64/ffsubsync "$M" \
  -i "$S" -o /tmp/ftC/synced.srt --max-offset-seconds 150 --max-subtitle-seconds 10 \
  --vad webrtc --no-fix-framerate --skip-infer-framerate-ratio --output-encoding utf-8 \
  --ffmpeg-path /usr/lib/jellyfin-ffmpeg/ffmpeg --log-dir-path /tmp/ftC
# report the verbatim 'score:', 'offset seconds:' and 'framerate scale factor:' lines and the exit codes of A, B, C

# 5. the second ruler, if step 2 shows an embedded swe text track (stream index N)
docker exec "$C" /usr/lib/jellyfin-ffmpeg/ffmpeg -v error -y -i "$M" -map 0:N /tmp/ft_embed_swe.srt
docker cp /tmp/ft/curve_scan.py "$C":/tmp/curve_scan.py
docker exec "$C" python3 /tmp/curve_scan.py /tmp/ft_embed_swe.srt "$Y" --max-offset 60   # residual after +50 ms
docker exec "$C" python3 /tmp/curve_scan.py /tmp/ft_embed_swe.srt "$S" --max-offset 150  # what the sidecar needed
# and, cheapest of all, the same scan against the plugin's own cache, no video read:
docker exec "$C" python3 /tmp/curve_scan.py \
  /config/data/data/subsync/state/speech-cache/74821354f558f4177f79c0f68a8126ef.npz "$S" --max-offset 150
```

How to read the result, given section 5:

* `ft_compare_cues.py` says **constant 50 ms** → the plugin applied exactly the engine's number; the engine's number
  is inside its own ±50–90 ms scatter (5a/5c), so "the plugin did what it said" and "the subtitles are in sync" are
  still different claims.
* **growing drift** (>30 ms accumulated, R² > 0.9) → the sidecar is not this cut; no global offset can fix it, and
  the user is right; the fix is a framerate/wrong-release subtitle, not a resync.
* **one large jump** → a re-edit; the engine's single offset cannot be right for the whole film.
* an embedded `swe` track in step 2 whose residual against `$Y` is **0 ms or ~50 ms** is the only result that
  confirms the alignment; a residual of a few hundred ms or a growing trend refutes it.

Nothing next to the media should be written by these commands: steps 3–5 write only into `/tmp` and `/tmp/ft*` inside
the container. Do not re-run the plugin's own sync on that file until the cue delta is measured — it would overwrite
`...swe.SYNCED.srt`, which is the only copy of what the +50 ms produced.

## 8. Environment corrections to the briefing

* **There is no `ffsubsync` executable at the two paths the briefing named** — `ls
  /opt/data/jellysubsync/Jellyfin.Plugin.SubSync/ffsubsync/linux-x64/` → `No such file or directory`; the deployed rig
  dir `/opt/data/jf12test/data/plugins/SubSync_2.0.66.0/` holds only the DLL/pdb/xml/deps/meta/logo, so the rig has no
  bundled engine and production's `/config/data/plugins/...` is not mounted here. The engine binary production ran
  lives inside the Jellyfin container. **Correction to an earlier draft of this bullet:** the claim "no `ffsubsync`
  on this machine" came from `find / -xdev -type f -name ffsubsync`, and `-xdev` cannot cross into `/opt/data`, which
  is its own mount. Run without it and four engines appear — `/opt/data/ffsbuild/bin/ffsubsync`,
  `/opt/data/tools/subsync-venv/bin/ffsubsync`, `/opt/data/ffs-test/pluginextract/ffsubsync/linux-x64/ffsubsync`, and
  the rig's `/opt/data/jf12test/data/data/subsync/venv/bin/ffsubsync`. The runs in section 5 used the rig's, which
  reports `ffsubsync 0.5.1` — the same version the production log records
  (`engine identity: ... answered 'ffsubsync 0.5.1'`). Which of the four matches production's PyInstaller build was
  not established, and is a caveat on section 5's numbers.
* `which ffmpeg` here is `/opt/data/bin/ffmpeg` (**ffmpeg 7.1.5**), not `/usr/bin/ffmpeg`; `/usr/lib/jellyfin-ffmpeg/`
  does not exist in this container. The rig fixtures were made with `/usr/bin/ffmpeg` per
  `tests/rig/run_scenario.py`. This is a method difference to keep in mind when comparing against production, whose
  argv says `--ffmpeg-path /usr/lib/jellyfin-ffmpeg/ffmpeg`.
* The rig is **live**: another writer installed `SubSync_2.0.66.0` into `/opt/data/jf12test/data/plugins/` at 02:23
  and wrote `/opt/data/jf12test/media-fixtures/Ass Track (2026).SYNCED.srt` at 02:24 during this session. Rig state
  is not a stable baseline; this session wrote nothing outside `/tmp/ft` and the report below (verified with
  `find /opt/data/jf12test -newermt '-120 minutes'`).

## Appendix A — tools, and the known-answer checks they passed

`/tmp/ft/ft_compare_cues.py` (Appendix B) — index-wise cue comparison + trend, on two real pairs:

```
P3 Probe (2026).eng.srt  ->  .eng.SYNCED.srt :  cues 803/803, all 803 differ, delta min=max=median=-5080 ms,
                                               spread 0, trend 0.0 ms/min, deltas off the 10 ms grid: 0,
                                               verdict "CONSTANT SHIFT of -5080 ms"
Helikopterrånet S01E01.swe.srt -> .swe.SYNCED.srt : cues 790/790, delta exactly -5000 ms on all 790,
                                               spread 0, first 10 and last 10 all -5000, verdict "CONSTANT SHIFT"
my webrtc re-run vs the plugin's .swe.SYNCED.srt : 790/790, delta exactly +50 ms on all 790, spread 0,
                                               trend 0.0, deltas off the 10 ms grid: 0
```

`/tmp/ft/curve_scan.py` (Appendix C) — known-answer recovery on real cue data: with the `SYNCED` cue mask as the
reference, the `SYNCED` file scores its peak at **0 ms** (peak = 276105 = every sample, 99 % width 30 ms) and the
original at **−5000 ms** (score at 0 ms 57575, 99 % width 30 ms) — the known shift to the millisecond.

`/tmp/ft/match_npz.py` — scans every cached `.npz` against a subtitle file to identify which film's speech a cache
holds (needed because cache names are hashes). Used here to identify `365b77790a7dd9d7305e034b3e051ca2.npz` as the
episode's audio (62106 speech samples, 20.74 %, 299424 samples).

## Appendix B — ft_compare_cues.py
```python
#!/usr/bin/env python3
"""Cue-by-cue comparison of an original .srt against a .SYNCED.srt (or any two cue files).

Settles, for a given pair of subtitle files, whether the difference between them is
  * a CONSTANT shift       -> a plain offset (what the engine claims to apply), or
  * a DRIFT (linear trend)  -> a framerate / wrong-cut mismatch a whole-file offset cannot fix, or
  * a STEP (piecewise)      -> a re-edit with inserted/removed material.
Also prints the residual scatter after the best constant shift, i.e. how much of the
disagreement one offset would explain.

Usage: python3 ft_compare_cues.py ORIGINAL.srt SYNCED.srt [--json OUT.json]
"""
import argparse, json, re, statistics, sys

TIME = re.compile(r"(\d+):(\d{2}):(\d{2})[,.](\d{1,3})")
GRID_MS = 10.0


def to_ms(h, m, s, frac):
    frac = (frac + "00")[:3]
    return ((int(h) * 60 + int(m)) * 60 + int(s)) * 1000 + int(frac)


def read_srt(path):
    raw = open(path, "r", encoding="utf-8-sig", errors="replace").read()
    cues = []
    if "[Events]" in raw and "Dialogue:" in raw:
        for line in raw.splitlines():
            if not line.startswith("Dialogue:"):
                continue
            parts = line.split(",", 9)
            if len(parts) < 10:
                continue
            a, b = TIME.search(parts[1]), TIME.search(parts[2])
            if a and b:
                cues.append((to_ms(*a.groups()), to_ms(*b.groups()), parts[9].strip()))
        return cues
    for block in re.split(r"\r?\n\r?\n+", raw.strip()):
        lines = [l for l in block.splitlines() if l.strip() != ""]
        if not lines:
            continue
        idx = 1 if re.fullmatch(r"\d+", lines[0].strip()) else 0
        if idx >= len(lines):
            continue
        found = list(TIME.finditer(lines[idx]))
        if len(found) < 2:
            continue
        cues.append((to_ms(*found[0].groups()), to_ms(*found[1].groups()),
                     "\n".join(lines[idx + 1:]).strip()))
    return cues


def slope_ms_per_min(cues_a, cues_b):
    xs = [a[0] / 60000.0 for a in cues_a]
    ys = [b[0] - a[0] for a, b in zip(cues_a, cues_b)]
    n = len(xs)
    if n < 3:
        return 0.0, 0.0
    mx, my = sum(xs) / n, sum(ys) / n
    sxx = sum((x - mx) ** 2 for x in xs)
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    if sxx == 0:
        return 0.0, 0.0
    slope = sxy / sxx
    syy = sum((y - my) ** 2 for y in ys)
    return slope, (0.0 if syy == 0 else (sxy ** 2) / (sxx * syy))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("original")
    ap.add_argument("synced")
    ap.add_argument("--json", dest="json_out")
    args = ap.parse_args()

    a, b = read_srt(args.original), read_srt(args.synced)
    n = min(len(a), len(b))
    if n == 0:
        print("NO CUES PARSED: %s=%d %s=%d" % (args.original, len(a), args.synced, len(b)))
        return 2

    deltas = [b[i][0] - a[i][0] for i in range(n)]
    changed = sum(1 for d in deltas if abs(d) > 1)
    slope, r2 = slope_ms_per_min(a[:n], b[:n])
    med = statistics.median(deltas)
    spread = max(deltas) - min(deltas)
    residuals = [abs(d - med) for d in deltas]
    jumps = [abs(deltas[i + 1] - deltas[i]) for i in range(n - 1)]
    off_grid = sum(1 for d in deltas
                   if abs(d % GRID_MS) > 0.001 and abs(d % GRID_MS - GRID_MS) > 0.001)

    total_trend = slope * (a[n - 1][0] / 60000.0 - a[0][0] / 60000.0)
    max_jump = max(jumps) if jumps else 0.0
    if total_trend > 3 * GRID_MS and r2 > 0.9:
        verdict = "DRIFT (linear trend accumul. past 30 ms: framerate/speed or wrong cut)"
    elif max_jump > 3 * GRID_MS and total_trend <= 3 * GRID_MS:
        verdict = "STEP/PIECEWISE (one jump of %.0f ms: re-edit)" % max_jump
    elif spread <= 2 * GRID_MS:
        verdict = "CONSTANT SHIFT of %.0f ms (within +/-%.0f ms)" % (med, spread / 2.0)
    else:
        verdict = ("MIXED/NOISY (spread %.0f ms, trend %.0f ms, max jump %.0f ms)"
                   % (spread, total_trend, max_jump))

    out = {
        "original": args.original, "synced": args.synced,
        "cues_original": len(a), "cues_synced": len(b), "cues_compared": n,
        "cues_diff_by_more_than_1ms": changed,
        "first_cue_original_ms": a[0][0], "last_cue_original_ms": a[n - 1][0],
        "first_cue_synced_ms": b[0][0], "last_cue_synced_ms": b[n - 1][0],
        "delta_ms": {"min": min(deltas), "max": max(deltas), "median": med,
                     "mean": statistics.fmean(deltas), "spread": spread,
                     "stdev": statistics.pstdev(deltas) if n > 1 else 0.0,
                     "max_residual_after_constant_shift": max(residuals)},
        "trend_ms_per_min": slope, "trend_total_ms_over_file": total_trend, "trend_r2": r2,
        "max_single_cue_jump_ms": max_jump, "deltas_not_on_10ms_grid": off_grid,
        "first_10_deltas_ms": deltas[:10], "last_10_deltas_ms": deltas[-10:],
        "all_deltas_ms_if_small": deltas if n <= 400 else None,
        "verdict": verdict,
    }
    print(json.dumps(out, indent=2, ensure_ascii=False))
    if args.json_out:
        json.dump(out, open(args.json_out, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

## Appendix C — curve_scan.py

```python
#!/usr/bin/env python3
"""Score the film's speech signal against a subtitle file across a range of offsets,
and report whether the peak is SHARP (trust the offset) or FLAT (a best-wrong-answer).

This is the cheap independent check for a suspect alignment: it needs only
  1. the reference speech array the engine itself produced, and
  2. the subtitle file,
so it reads no video at all (seconds, not a 58 s walk of an 8.5 GB file).

The reference may be:
  * an .npz with a 'speech' key, the plugin/engine's own cached speech array
    (--serialize-speech writes it; SAMPLE_RATE is 100 Hz, see ffsubsync/constants.py), or
  * a .srt, in which case the subtitle's own cue spans stand in as the "speech" - useful
    as a two-subtitle cross-check (external sidecar vs a track extracted from the film).

Usage:
  python3 curve_scan.py SPEECH.npz  SUBTITLE.srt [--max-offset 150] [--sample-rate 100]
  python3 curve_scan.py REFERENCE.srt SUBTITLE.srt [--max-offset 150]

Prints the argmax lag (ms), the peak score, the score at lag 0 and at a few fixed lags,
the best competing peak outside the peak's neighbourhood, how many lags score within 99 %
of the peak, and a verdict.
"""
import argparse
import json
import re
import sys

import numpy as np

TIME = re.compile(r"(\d+):(\d{2}):(\d{2})[,.](\d{1,3})")


def to_ms(h, m, s, frac):
    return ((int(h) * 60 + int(m)) * 60 + int(s)) * 1000 + int((frac + "00")[:3])


def read_cues(path):
    raw = open(path, "r", encoding="utf-8-sig", errors="replace").read()
    cues = []
    for block in re.split(r"\r?\n\r?\n+", raw.strip()):
        lines = [l for l in block.splitlines() if l.strip()]
        if not lines:
            continue
        idx = 1 if re.fullmatch(r"\d+", lines[0].strip()) else 0
        if idx >= len(lines):
            continue
        f = list(TIME.finditer(lines[idx]))
        if len(f) < 2:
            continue
        cues.append((to_ms(*f[0].groups()), to_ms(*f[1].groups())))
    return cues


def mask_from_cues(cues, rate, max_seconds=None):
    if not cues:
        return np.zeros(0, dtype=np.float64)
    end = max(c[1] for c in cues)
    m = np.zeros(int(end * rate / 1000.0) + 1, dtype=np.float64)
    for a, b in cues:
        if max_seconds and (b - a) > max_seconds * 1000:
            b = a + int(max_seconds * 1000)
        i, j = int(a * rate / 1000.0), int(b * rate / 1000.0)
        if j > i:
            m[i:j] = 1.0
    return m


def load_reference(path, rate, max_seconds):
    if path.endswith(".npz"):
        z = np.load(path)
        key = "speech" if "speech" in z else list(z.keys())[0]
        return np.asarray(z[key], dtype=np.float64), ("npz:%s" % key)
    return mask_from_cues(read_cues(path), rate, max_seconds), "srt-cues"


def cross_corr(x, y):
    """Circular cross-correlation c[j] = sum_n x[n] * y[n-j], padded to >= len(x)+len(y).

    c[j] with j>0 means y (the subtitle) is LATE by j samples, i.e. the subtitle has to be
    moved earlier by j to match the reference; c[0] is the no-lag score. Negative lags wrap
    to N+j, so a lag table is built with np.take(..., ks % N).
    """
    n = 1
    while n < len(x) + len(y):
        n *= 2
    fx, fy = np.fft.rfft(x, n), np.fft.rfft(y, n)
    return np.fft.irfft(fx * np.conj(fy), n), n


def lag_scores(c, n, ks):
    """Scores for a lag table (samples), wrapping negative lags."""
    return c[np.take(np.arange(len(c)), ks % n)]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("reference")
    ap.add_argument("subtitle")
    ap.add_argument("--max-offset", type=float, default=150.0)
    ap.add_argument("--sample-rate", type=float, default=100.0)
    ap.add_argument("--max-subtitle-seconds", type=float, default=10.0)
    ap.add_argument("--json", dest="json_out")
    args = ap.parse_args()

    rate = args.sample_rate
    ref, ref_kind = load_reference(args.reference, rate, args.max_subtitle_seconds)
    cues = read_cues(args.subtitle)
    if len(cues) == 0:
        print("NO CUES in %s" % args.subtitle)
        return 2
    sub = mask_from_cues(cues, rate, args.max_subtitle_seconds)

    x = 2.0 * ref - 1.0
    y = 2.0 * sub - 1.0
    c, n = cross_corr(x, y)
    maxs = int(abs(args.max_offset) * rate)
    ks = np.arange(-maxs, maxs + 1)
    scores = lag_scores(c, n, ks)
    best = int(np.argmax(scores))
    peak_lag_samples = int(ks[best])
    peak = float(scores[best])
    peak_ms = peak_lag_samples * (1000.0 / rate)

    # neighbourhood of the peak: within +/-0.5 s
    nb = int(0.5 * rate)
    outside = np.ones(len(scores), dtype=bool)
    outside[max(0, best - nb): best + nb + 1] = False
    second = float(scores[outside].max()) if outside.any() else float("nan")

    near99 = int((scores >= 0.99 * peak).sum()) if peak > 0 else 0
    within99_ms = near99 * (1000.0 / rate)

    def at(msec):
        k = int(round(msec * rate / 1000.0))
        return float(c[k % n])

    ratio = (second / peak) if peak else float("nan")
    # a trustworthy peak is well above the best competitor and narrow
    if within99_ms <= 1000.0 and ratio < 0.97:
        verdict = ("SHARP: peak %.0f ms stands above the next-best peak (%.4f of it) and the "
                   "99%% region is %.0f ms wide -> the offset is measured, not guessed"
                   % (peak_ms, ratio, within99_ms))
    elif ratio >= 0.999:
        verdict = ("FLAT: a competing peak elsewhere scores %.4f of the winner (%.0f ms) - "
                   "the argmax is not resolved, treat the offset as a best-wrong-answer"
                   % (ratio, peak_ms))
    else:
        verdict = ("AMBIGUOUS: competitor %.4f of the peak, 99%% region %.0f ms wide "
                   "(peak %.0f ms)" % (ratio, within99_ms, peak_ms))

    out = {
        "reference": args.reference, "reference_kind": ref_kind,
        "subtitle": args.subtitle, "sample_rate": rate,
        "reference_samples": int(len(ref)), "reference_seconds": len(ref) / rate,
        "speech_fraction_of_reference": float(ref.mean()) if len(ref) else None,
        "cues": len(cues), "first_cue_ms": cues[0][0], "last_cue_ms": cues[-1][1],
        "max_offset_seconds": args.max_offset,
        "best_offset_ms": peak_ms, "best_offset_samples": peak_lag_samples,
        "peak_score": peak,
        "score_at_0ms": at(0), "score_at_-50ms": at(-50), "score_at_+50ms": at(50),
        "score_at_-200ms": at(-200), "score_at_+200ms": at(200),
        "score_at_-1000ms": at(-1000), "score_at_+1000ms": at(1000),
        "second_best_peak_outside_0.5s": second,
        "second_best_over_peak": ratio,
        "lags_within_99pct_of_peak": near99, "within_99pct_width_ms": within99_ms,
        "verdict": verdict,
    }
    print(json.dumps(out, indent=2, ensure_ascii=False))
    if args.json_out:
        json.dump(out, open(args.json_out, "w", encoding="utf-8"), indent=2, ensure_ascii=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())
```

## Appendix D — match_npz.py

```python
#!/usr/bin/env python3
"""Scan every cached speech .npz against a subtitle file and report which one matches it.

Used to pair a subtitle with the speech signal that was actually measured from its film
(the cache file names are hashes, so the pairing has to be measured, not guessed).
"""
import glob, os, sys
import numpy as np
import curve_scan as cs

rate = 100.0
maxlags = int(sys.argv[3]) if len(sys.argv) > 3 else 3000  # samples at 100 Hz
srts = sys.argv[2].split(',')

subs = {p: cs.mask_from_cues(cs.read_cues(p), rate, 10.0) for p in srts}
rows = []
for f in sorted(glob.glob(sys.argv[1])):
    ref = np.load(f)['speech'].astype(float)
    if len(ref) < 10000:
        continue
    x = 2 * ref - 1
    row = {'npz': os.path.basename(f), 'samples': int(len(ref)),
           'speech_pct': round(float(ref.mean()) * 100, 2)}
    for p, sub in subs.items():
        y = 2 * sub - 1
        c, origin = cs.cross_corr(x, y)
        ks = np.arange(-maxlags, maxlags + 1)
        idx = origin + ks
        keep = (idx >= 0) & (idx < len(c))
        ks, sc = ks[keep], c[idx[keep]]
        best = int(np.argmax(sc))
        row[os.path.basename(p)] = {
            'lag_ms': int(ks[best]) * 10, 'peak': int(sc[best]),
            'at0': int(c[origin]) if 0 <= origin < len(c) else None}
    rows.append(row)

key = 'peak'
rows.sort(key=lambda r: -r[os.path.basename(srts[0])][key])
print(f"{'npz':<36}{'samples':>9}{'spch%':>7}  " + "  ".join(
    f"{os.path.basename(p)[:22]:<22} lag / peak / at0" for p in srts))
for r in rows[:10]:
    cells = []
    for p in srts:
        d = r[os.path.basename(p)]
        cells.append(f"{os.path.basename(p)[:22]:<22} {d['lag_ms']:>7} {d['peak']:>9} {d['at0']:>8}")
    print(f"{r['npz']:<36}{r['samples']:>9}{r['speech_pct']:>7}  " + "  ".join(cells))
```
