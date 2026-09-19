# SubSync plugin log analysis — 2026-09-18 runs (through 2026-09-19 00:20:40Z)

Read-only pass. Every number below is produced by `tests/backend/analyse_log_2026_09_18.py` from a frozen snapshot of `/subsync-logs/subsync.log`; the same numbers are in `tests/backend/log-analysis-2026-09-18.json`. Nothing was fixed, built or committed by this pass.

## Logs read

| item | value |
|---|---|
| log analysed | `/subsync-logs/subsync.log` (live, still being appended to; copied to the frozen snapshot below before any count) |
| frozen snapshot every number comes from | `/tmp/logwork/snapshot.log`, md5 `7694cdd7a229f14e99182a74ed9d41c6` |
| bytes | 3814344 |
| lines (records) | **13394** |
| lines with a timestamp header | 13353 |
| lines without a header (stack-trace continuation lines) | 41 — line numbers [4791, 4792, 4793, 4794, 4819, 4820, 4821, 4822, 8095, 8096, 8097, 8098, 9559, 9560, 9561, 9562, 11846, 11847, 11848, 11849, 11876, 11877, 11878, 11879, 11908, 11909, 11910, 11911, 11942, 11943, 11944, 11945, 12009, 12010, 12011, 12012, 12102, 12103, 12104, 12105, 12106] |
| first line | `2026-09-18 03:29:27.363Z INFO  [0cdde638a8fd47869ea4f1805c3a4828] ffsubsync exit=0 after 1380 ms` |
| last line | `2026-09-19 00:20:40.545Z INFO  extract lane: BoJack Horseman (2014) - S03E08 - Old Acquaintance (1080p NF WEB-DL x265 Ghost).mkv -> 3/3 subtitle(s), 3,5 MB, 1675 reads, 20332 ms, ok=True, reason= prefetched=1654 ranges/3,1 MB unused=0 ranges/2,8 MB bytesTwice=0,00 MB memoryReads=11659 plan=cue-indexed expected=3,17 MB/1974 reads missed=0 indexedMisses=0 walked=0 storage=7,24 ms/read 7,1 MB/s (from the Matroska index (SeekHead))` |
| first timestamp | `2026-09-18 03:29:27.363Z` |
| last timestamp | `2026-09-19 00:20:40.545Z` |
| lines dated 2026-09-18 | **9591** |
| lines dated 2026-09-19 | **3762** |
| timestamps monotonic | True |
| lines after the stated 2026-09-19 00:19Z window edge | 92 (first at `2026-09-19 00:20:00.296Z`) |

Two things about the window that the file itself settles:
- **The file does not contain 2026-09-18 00:00–03:29Z.** It starts at `2026-09-18 03:29:27.363Z` — line 1 is a job *completion*, not a queue event, i.e. the run in flight was queued in the previous, rotated file. So "all of 2026-09-18" is not in this file: 03:29:27.363Z → 2026-09-19 00:20:40.545Z is what was analysed. What would settle the missing 3.5 hours: `subsync.log.1` (excluded by the instruction for this pass).
- The file was still being appended to while it was read (it grew from 3 809 779 bytes to 3 814 344 bytes between the first `ls` and the `cp`). All numbers here therefore describe the frozen snapshot, whose last line is `2026-09-19 00:20:40.545Z INFO  extract lane: BoJack Horseman (2014) - S03E08 - Old Acquain…`.
- 92 lines fall after 00:19:59.999Z (the stated window edge); the last is 2026-09-19 00:20:40.545Z. They are included, and the window edge is called out again in the run-accounting section because those minutes are the tail of a live run.

Line-count and window commands:
```
$ cp /subsync-logs/subsync.log /tmp/logwork/snapshot.log && md5sum /tmp/logwork/snapshot.log
7694cdd7a229f14e99182a74ed9d41c6  /tmp/logwork/snapshot.log
$ wc -l /tmp/logwork/snapshot.log
13394 /tmp/logwork/snapshot.log
```

## Extraction method split

Completion lines: **976** jobs wrote a `job <id> completed:` line, each carrying one `extraction=` value. Grouped:

| `extraction=` class | jobs |
|---|---|
| served from the extracted-subtitle cache, no read this run | 745 |
| read through the container index (seekhead-cues), N ms | 12 |
| n/a (nothing had to be extracted) | 219 |
| **total** | **976** |

So **745 jobs were served from the extraction cache and 12 did a real read** of the container; 219 needed no extraction at all (the subtitle was already in sync). The 12 real reads, each verbatim:

| line | job | `extraction=` value |
|---|---|---|
| 2103 | `2b517ddeb716429f8be560f60fd6e51c` | `read through the container index (seekhead-cues), 12601 ms` |
| 2137 | `0d8a3d7ed3c243f98c4d0d6cffd074fe` | `read through the container index (seekhead-cues), 9992 ms` |
| 5167 | `25ec121385574c9b8f88e5f3c77c3824` | `read through the container index (seekhead-cues), 119 ms` |
| 5203 | `d0d0ba29877b4c9388561a116851767f` | `read through the container index (seekhead-cues), 42448 ms` |
| 5461 | `8a011ee650e940ac95143e3593a81ca2` | `read through the container index (seekhead-cues), 1319 ms` |
| 5765 | `c35bf99dbbb54cc8be0b2a79b0c7e36d` | `read through the container index (seekhead-cues), 7283 ms` |
| 6681 | `8bc0d5af2a2e472ea0cb76d3e3b60a93` | `read through the container index (seekhead-cues), 23631 ms` |
| 6790 | `5ff1473b894f4d89a43f7d02e4463dd7` | `read through the container index (seekhead-cues), 9148 ms` |
| 6836 | `1ef8e720002642f6a481bebdca42643c` | `read through the container index (seekhead-cues), 5225 ms` |
| 7180 | `3e9e9f5a844e44aba7e929b4127cb166` | `read through the container index (seekhead-cues), 7922 ms` |
| 7194 | `943d04bd700d4fd2be28c93541b0f55f` | `read through the container index (seekhead-cues), 12157 ms` |
| 7598 | `2efac5cba5fd475b899f53ffdc6ec01c` | `read through the container index (seekhead-cues), 1052 ms` |

Every distinct `extraction=` value with its job count:

```
n/a  <- 219 job(s)
read through the container index (seekhead-cues), 1052 ms  <- 1 job(s)
read through the container index (seekhead-cues), 119 ms  <- 1 job(s)
read through the container index (seekhead-cues), 12157 ms  <- 1 job(s)
read through the container index (seekhead-cues), 12601 ms  <- 1 job(s)
read through the container index (seekhead-cues), 1319 ms  <- 1 job(s)
read through the container index (seekhead-cues), 23631 ms  <- 1 job(s)
read through the container index (seekhead-cues), 42448 ms  <- 1 job(s)
read through the container index (seekhead-cues), 5225 ms  <- 1 job(s)
read through the container index (seekhead-cues), 7283 ms  <- 1 job(s)
read through the container index (seekhead-cues), 7922 ms  <- 1 job(s)
read through the container index (seekhead-cues), 9148 ms  <- 1 job(s)
read through the container index (seekhead-cues), 9992 ms  <- 1 job(s)
served from the extracted-subtitle cache, no read this run  <- 745 job(s)
```

Per **reference** method. Two line families carry it:
- `[jobid] reference: method=…` (the per-job decision): **subtitle** 597, **audio** 56, **speech-cache** 5
- bare `reference: method=…` (the file-level probe): **cache** 595, **seekhead-cues** 29, **index-none** 5, **metadata-scan** 1
- bare `extract: method=…` (the file-level extraction): **cache** 763, **seekhead-cues** 13, **ffmpeg** 1

Reference from cache vs a real read: the file-level reference resolution is answered by the subtitle cache in 595 lines and by the cue index (`seekhead-cues`) in 29; `metadata-scan` once, and `index-none` 5 times, which is the WARN path (no subtitle blocks found / unsupported block encoding) and sends that file to the audio.

Job bookkeeping behind those numbers: 1494 job ids appear in the file, 1268 carry a `queued: job=…` line, 1000 ran the engine (`ffsubsync start:`), and 1001 reached a terminal line. The 226 terminal lines that have no `queued:` line in this file are the jobs queued in the previous log file — they are the very first lines of this one.

## WARN and ERROR lines, verbatim

**50 WARN lines and 10 ERROR lines.** All 50 WARN texts and all 10 ERROR texts are distinct — no exact duplicate message occurs twice anywhere in the day.

```
$ grep -nE ' (WARN|ERROR) ' /tmp/logwork/snapshot.log | wc -l
60
$ grep -cE ' WARN ' /tmp/logwork/snapshot.log
50
$ grep -cE ' ERROR ' /tmp/logwork/snapshot.log
10
```

### WARN — 5 families, each with its exact count, one full verbatim quote, and the full message set

**A. `extract: <file> read X MB past the fetch …` — a fetched range did not cover the read it was made for** — count **20**, lines: [773, 7213, 7256, 7276, 7303, 7311, 8091, 8107, 8345, 8482, 8496, 8510, 8524, 8537, 8551, 8565, 8578, 8613, 9808, 9822]

One of them, verbatim (line 773):

```
2026-09-18 03:31:29.034Z WARN  extract: Take.Care.of.Maya.2023.1080p.NF.WEB-DL.DDP5.1.x264-CMRG.mkv read 0,00 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 4999 range(s)/8.07 MB, 0 unused (7.26 MB), read past the fetch 0.00 MB, fetched over the location reads 0.00 MB
```

The remaining 19 of that family, verbatim:

```
2026-09-18 12:07:13.551Z WARN  extract: solsidan.s03e01.swedish.1080p.bluray.x264-prince.mkv read 0,12 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 587 range(s)/0.73 MB, 0 unused (0.11 MB), read past the fetch 0.12 MB, fetched over the location reads 0.00 MB
2026-09-18 12:07:25.025Z WARN  extract: solsidan.s03e04.swedish.1080p.bluray.x264-prince.mkv read 0,00 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 809 range(s)/1.21 MB, 0 unused (1.08 MB), read past the fetch 0.00 MB, fetched over the location reads 0.00 MB
2026-09-18 12:07:42.805Z WARN  extract: solsidan.s03e05.repack.swedish.1080p.bluray.x264-prince.mkv read 0,11 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 814 range(s)/1.21 MB, 0 unused (0.59 MB), read past the fetch 0.11 MB, fetched over the location reads 0.49 MB
2026-09-18 12:09:38.877Z WARN  extract: solsidan.s03e08.swedish.1080p.bluray.x264-prince.mkv read 0,10 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 574 range(s)/0.72 MB, 0 unused (0.12 MB), read past the fetch 0.10 MB, fetched over the location reads 0.01 MB
2026-09-18 12:10:06.900Z WARN  extract: solsidan.s03e07.swedish.1080p.bluray.x264-prince.mkv read 0,08 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 605 range(s)/0.78 MB, 0 unused (0.22 MB), read past the fetch 0.08 MB, fetched over the location reads 0.01 MB
2026-09-18 12:13:44.790Z WARN  extract: solsidan.s03e09.swedish.1080p.bluray.x264-prince.mkv read 0,00 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 709 range(s)/1.04 MB, 0 unused (0.92 MB), read past the fetch 0.00 MB, fetched over the location reads 0.01 MB
2026-09-18 12:13:51.873Z WARN  extract: solsidan.s03e01.swedish.1080p.bluray.x264-prince.mkv read 0,17 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 552 range(s)/0.66 MB, 0 unused (0.02 MB), read past the fetch 0.17 MB, fetched over the location reads 0.00 MB
2026-09-18 12:14:45.372Z WARN  extract: solsidan.s03e10.swedish.1080p.bluray.x264-prince.mkv read 0,03 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 573 range(s)/0.77 MB, 0 unused (0.45 MB), read past the fetch 0.03 MB, fetched over the location reads 0.00 MB
2026-09-18 12:18:03.891Z WARN  extract: solsidan.s03e02.swedish.1080p.bluray.x264-prince.mkv read 0,09 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 528 range(s)/0.63 MB, 0 unused (0.02 MB), read past the fetch 0.09 MB, fetched over the location reads 0.00 MB
2026-09-18 12:18:21.396Z WARN  extract: solsidan.s03e03.swedish.1080p.bluray.x264-prince.mkv read 0,09 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 526 range(s)/0.62 MB, 0 unused (0.05 MB), read past the fetch 0.09 MB, fetched over the location reads 0.00 MB
2026-09-18 12:18:32.122Z WARN  extract: solsidan.s03e04.swedish.1080p.bluray.x264-prince.mkv read 0,07 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 541 range(s)/0.64 MB, 0 unused (0.04 MB), read past the fetch 0.07 MB, fetched over the location reads 0.00 MB
2026-09-18 12:18:50.361Z WARN  extract: solsidan.s03e05.repack.swedish.1080p.bluray.x264-prince.mkv read 0,00 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 545 range(s)/0.65 MB, 0 unused (0.53 MB), read past the fetch 0.00 MB, fetched over the location reads 0.00 MB
2026-09-18 12:19:03.654Z WARN  extract: solsidan.s03e06.swedish.1080p.bluray.x264-prince.mkv read 0,14 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 561 range(s)/0.67 MB, 0 unused (0.10 MB), read past the fetch 0.14 MB, fetched over the location reads 0.00 MB
2026-09-18 12:19:20.286Z WARN  extract: solsidan.s03e07.swedish.1080p.bluray.x264-prince.mkv read 0,14 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 540 range(s)/0.64 MB, 0 unused (0.02 MB), read past the fetch 0.14 MB, fetched over the location reads 0.00 MB
2026-09-18 12:19:31.492Z WARN  extract: solsidan.s03e08.swedish.1080p.bluray.x264-prince.mkv read 0,09 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 534 range(s)/0.63 MB, 0 unused (-0.01 MB), read past the fetch 0.09 MB, fetched over the location reads 0.00 MB
2026-09-18 12:19:45.332Z WARN  extract: solsidan.s03e09.swedish.1080p.bluray.x264-prince.mkv read 0,11 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 451 range(s)/0.53 MB, 0 unused (0.06 MB), read past the fetch 0.11 MB, fetched over the location reads 0.00 MB
2026-09-18 12:19:49.954Z WARN  extract: solsidan.s03e10.swedish.1080p.bluray.x264-prince.mkv read 0,07 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 433 range(s)/0.51 MB, 0 unused (0.07 MB), read past the fetch 0.07 MB, fetched over the location reads 0.00 MB
2026-09-19 00:10:10.873Z WARN  extract: American.Nightmare.2024.S01E02.1080p.WEB.h264-GP-TV-NLsubs.mkv read 0,00 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 2135 range(s)/4.17 MB, 0 unused (3.46 MB), read past the fetch 0.00 MB, fetched over the location reads 0.00 MB
2026-09-19 00:10:11.151Z WARN  extract: American.Nightmare.2024.S01E01.1080p.WEB.h264-GP-TV-NLsubs.mkv read 0,00 MB past the fetch (a fetched range that did not cover the read it was made for) - fetched 2544 range(s)/4.87 MB, 0 unused (3.92 MB), read past the fetch 0.00 MB, fetched over the location reads 0.00 MB
```

**B. `extract plan: <file> … - this pass …` — the pass missed its own plan** — count **20**, lines: [7030, 7144, 7209, 7244, 7265, 7299, 7307, 7316, 8079, 8105, 8337, 8480, 8494, 8508, 8522, 8535, 8549, 8563, 8576, 8611]

One of them, verbatim (line 7030):

```
2026-09-18 12:03:13.435Z WARN  extract plan: solsidan.s03e03.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.62 MB/518 read(s) (102909.8 ms), actual 38.88 MB/9861 read(s) (2980720.3 ms) - bytes 63.20x, reads 19.04x - this pass missed its own prediction
```

The remaining 19 of that family, verbatim:

```
2026-09-18 12:03:51.191Z WARN  extract plan: solsidan.s03e02.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.63 MB/526 read(s) (17396.4 ms), actual 40.92 MB/10363 read(s) (521462.0 ms) - bytes 65.30x, reads 19.70x - this pass missed its own prediction
2026-09-18 12:07:13.149Z WARN  extract plan: solsidan.s03e01.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.65 MB/544 read(s) (50372.8 ms), actual 41.55 MB/10530 read(s) (1481006.6 ms) - bytes 63.72x, reads 19.36x - this pass missed its own prediction
2026-09-18 12:07:23.440Z WARN  extract plan: solsidan.s03e04.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.64 MB/537 read(s) (36578.7 ms), actual 41.23 MB/10447 read(s) (1083552.1 ms) - bytes 64.75x, reads 19.45x - this pass missed its own prediction
2026-09-18 12:07:39.851Z WARN  extract plan: solsidan.s03e05.repack.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.65 MB/546 read(s) (520.1 ms), actual 40.29 MB/10224 read(s) (14824.4 ms) - bytes 62.33x, reads 18.73x - this pass missed its own prediction
2026-09-18 12:09:38.293Z WARN  extract plan: solsidan.s03e08.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.63 MB/530 read(s) (451.4 ms), actual 43.74 MB/11056 read(s) (14359.6 ms) - bytes 69.71x, reads 20.86x - this pass missed its own prediction
2026-09-18 12:10:05.966Z WARN  extract plan: solsidan.s03e07.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.64 MB/540 read(s) (439.6 ms), actual 41.65 MB/10552 read(s) (13076.5 ms) - bytes 64.94x, reads 19.54x - this pass missed its own prediction
2026-09-18 12:10:22.428Z WARN  extract plan: solsidan.s03e06.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.66 MB/560 read(s) (468.4 ms), actual 43.77 MB/11084 read(s) (14124.9 ms) - bytes 66.02x, reads 19.79x - this pass missed its own prediction
2026-09-18 12:13:40.506Z WARN  extract plan: solsidan.s03e09.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.56 MB/476 read(s) (79035.3 ms), actual 36.68 MB/9292 read(s) (2350286.0 ms) - bytes 65.08x, reads 19.52x - this pass missed its own prediction
2026-09-18 12:13:51.873Z WARN  extract plan: solsidan.s03e01.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.66 MB/552 read(s) (28196.5 ms), actual 42.10 MB/10668 read(s) (827619.4 ms) - bytes 63.60x, reads 19.33x - this pass missed its own prediction
2026-09-18 12:14:39.703Z WARN  extract plan: solsidan.s03e10.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.50 MB/425 read(s) (62280.7 ms), actual 36.22 MB/9145 read(s) (2044036.8 ms) - bytes 71.84x, reads 21.52x - this pass missed its own prediction
2026-09-18 12:18:03.891Z WARN  extract plan: solsidan.s03e02.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.63 MB/528 read(s) (856.8 ms), actual 42.03 MB/10635 read(s) (26264.5 ms) - bytes 66.79x, reads 20.14x - this pass missed its own prediction
2026-09-18 12:18:21.396Z WARN  extract plan: solsidan.s03e03.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.62 MB/526 read(s) (877.8 ms), actual 40.65 MB/10298 read(s) (26160.1 ms) - bytes 65.06x, reads 19.58x - this pass missed its own prediction
2026-09-18 12:18:32.121Z WARN  extract plan: solsidan.s03e04.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.64 MB/541 read(s) (12849.4 ms), actual 42.46 MB/10750 read(s) (388919.3 ms) - bytes 66.18x, reads 19.87x - this pass missed its own prediction
2026-09-18 12:18:50.361Z WARN  extract plan: solsidan.s03e05.repack.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.65 MB/546 read(s) (428.0 ms), actual 39.35 MB/9995 read(s) (11921.7 ms) - bytes 60.87x, reads 18.31x - this pass missed its own prediction
2026-09-18 12:19:03.654Z WARN  extract plan: solsidan.s03e06.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.67 MB/562 read(s) (471.2 ms), actual 44.82 MB/11340 read(s) (14491.5 ms) - bytes 67.35x, reads 20.18x - this pass missed its own prediction
2026-09-18 12:19:20.286Z WARN  extract plan: solsidan.s03e07.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.64 MB/540 read(s) (613.6 ms), actual 41.84 MB/10598 read(s) (18332.5 ms) - bytes 65.22x, reads 19.63x - this pass missed its own prediction
2026-09-18 12:19:31.492Z WARN  extract plan: solsidan.s03e08.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.63 MB/534 read(s) (354.4 ms), actual 44.43 MB/11226 read(s) (11364.0 ms) - bytes 70.27x, reads 21.02x - this pass missed its own prediction
2026-09-18 12:19:45.331Z WARN  extract plan: solsidan.s03e09.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.54 MB/452 read(s) (617.7 ms), actual 35.29 MB/8937 read(s) (18610.3 ms) - bytes 65.95x, reads 19.77x - this pass missed its own prediction
2026-09-18 12:19:49.954Z WARN  extract plan: solsidan.s03e10.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.51 MB/433 read(s) (376.3 ms), actual 36.95 MB/9328 read(s) (12365.9 ms) - bytes 71.93x, reads 21.54x - this pass missed its own prediction
```

**C. `reference: method=index-none … reason=… file=…` — the cue index could not read that track** — count **5**, lines: [1450, 2313, 3449, 8049, 12671]

One of them, verbatim (line 1450):

```
2026-09-18 03:32:29.284Z WARN  reference: method=index-none ms=14391 stream=0 reason=no subtitle blocks found file=/Media/TV Shows/The Trio/Season 1/The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv
```

The remaining 4 of that family, verbatim:

```
2026-09-18 03:53:25.241Z WARN  reference: method=index-none ms=14043 stream=3 reason=unsupported block encoding file=/media/synology/Syn-Filmer/The Lighthouse (2019)/The Lighthouse (2019) Bluray-1080p.mkv
2026-09-18 11:46:12.440Z WARN  reference: method=index-none ms=16160 stream=0 reason=no subtitle blocks found file=/Media/TV Shows/The Trio/Season 1/The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv
2026-09-18 12:13:21.502Z WARN  reference: method=index-none ms=14004 stream=0 reason=no subtitle blocks found file=/Media/TV Shows/The Trio/Season 1/The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv
2026-09-19 00:17:46.162Z WARN  reference: method=index-none ms=15282 stream=0 reason=no subtitle blocks found file=/Media/TV Shows/The Trio/Season 1/The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv
```

**D. `extract fallback to ffmpeg: …` — the Matroska route gave up on the file** — count **3**, lines: [2260, 7763, 9510]

One of them, verbatim (line 2260):

```
2026-09-18 03:46:10.328Z WARN  extract fallback to ffmpeg: file=/media/synology/Syn-Filmer/The Lighthouse (2019)/The Lighthouse (2019) Bluray-1080p.mkv sizeMb=8562,3 timeoutMinutes=20 reasons=matroska-index: unsupported block encoding [seekhead-cues: 123 clusters, 0 blocks, 2.3 MB in 1700 reads, 17180.7797 ms (locate 19.7391 ms, read 0 ms, 7827 cue points, 863 with a block offset) | 0.0 ms/read, 0 blocks/s, kernel 3347980 bytes in 2964 calls]
```

The remaining 2 of that family, verbatim:

```
2026-09-18 12:12:48.748Z WARN  extract fallback to ffmpeg: file=/media/synology2/Syn2-Serier/Thunder in My Heart/Season 1/Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv sizeMb=1035,1 timeoutMinutes=20 reasons=matroska-index: no subtitle blocks found [metadata-scan: 5 clusters, 0 blocks, 0.8 MB in 209 reads, 1973.9296 ms (locate 509.5378 ms, read 1284.5224 ms, 0 cue points, 0 with a block offset) | 6.1 ms/read, 0 blocks/s, kernel 3867123 bytes in 992 calls]
2026-09-18 14:18:07.623Z WARN  extract fallback to ffmpeg: file=/media/synology2/Syn2-Serier/Thunder in My Heart/Season 1/Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv sizeMb=1035,1 timeoutMinutes=20 reasons=matroska-index: no subtitle blocks found [metadata-scan: 5 clusters, 0 blocks, 0.8 MB in 209 reads, 10702.7416 ms (locate 784.2196 ms, read 8223.3985 ms, 0 cue points, 0 with a block offset) | 39.3 ms/read, 0 blocks/s, kernel 3718729 bytes in 2069 calls]
```

**E. `extract: rejected …` — a partial extraction was discarded** — count **2**, lines: [8093, 9557]

One of them, verbatim (line 8093):

```
2026-09-18 12:13:51.363Z WARN  extract: rejected file=/media/synology2/Syn2-Serier/Thunder in My Heart/Season 1/Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv stream=9 exit=0 reason=ffmpeg read a partial file - it reported 'ended prematurely' - so the 81 cue(s) it produced are only part of the track - discarded the partial extraction (5567 bytes) at /config/cache/subsync/shared/9f1e5589be7d7400/subtitle_9.srt
```

The remaining 1 of that family, verbatim:

```
2026-09-18 14:18:47.825Z WARN  extract: rejected file=/media/synology2/Syn2-Serier/Thunder in My Heart/Season 1/Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv stream=4 exit=0 reason=ffmpeg read a partial file - it reported 'ended prematurely' - so the 85 cue(s) it produced are only part of the track - discarded the partial extraction (5649 bytes) at /config/cache/subsync/shared/9f1e5589be7d7400/subtitle_4.srt
```

### ERROR — all 10 lines verbatim, each with the exception text that follows it

```
 4790 | 2026-09-18 11:57:23.642Z ERROR job 76073d68e8a34eaaa87cde48169d63d2 failed: mode=ultimate item=6df9163a-04df-6beb-239a-032754411577 stream=28
      + System.InvalidOperationException: Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE': the Jellyfin user has no write permission there (the folder may also be mounted read-only). Nothing was changed and the original subtitle is untouched. Fix the folder's permissions for the Jellyfin user (or how the library is mounted) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.
      +    at Jellyfin.Plugin.SubSync.Services.SyncedTargetNaming.RequireWritable(String directory)

 4818 | 2026-09-18 11:57:28.415Z ERROR job a135e064de9a45cbb59552639e198226 failed: mode=ultimate item=3a7b4d68-7958-7911-b227-fe8c7d3b6402 stream=28
      + System.InvalidOperationException: Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE': the Jellyfin user has no write permission there (the folder may also be mounted read-only). Nothing was changed and the original subtitle is untouched. Fix the folder's permissions for the Jellyfin user (or how the library is mounted) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.
      +    at Jellyfin.Plugin.SubSync.Services.SyncedTargetNaming.RequireWritable(String directory)

 8094 | 2026-09-18 12:13:51.366Z ERROR job 2360b3f2aa6844fa9a7df197c7cbfa83 failed: mode=ultimate item=48acc86c-9561-b8c9-c838-8a88bfa9f101 stream=9
      + System.InvalidOperationException: ffmpeg subtitle extraction failed: ffmpeg read a partial file - it reported 'ended prematurely' - so the 81 cue(s) it produced are only part of the track - discarded the partial extraction (5567 bytes) at /config/cache/subsync/shared/9f1e5589be7d7400/subtitle_9.srt
      +    at Jellyfin.Plugin.SubSync.Services.SubSyncService.ExtractSubtitleWithProgressAsync(String videoPath, Int32 streamIndex, String outputPath, Double durationSeconds, SyncJob job, CancellationToken cancellationToken)

 9558 | 2026-09-18 14:18:47.830Z ERROR job c67e8ddca5d149339144ea9837084831 failed: mode=ultimate item=48acc86c-9561-b8c9-c838-8a88bfa9f101 stream=4
      + System.InvalidOperationException: ffmpeg subtitle extraction failed: ffmpeg read a partial file - it reported 'ended prematurely' - so the 85 cue(s) it produced are only part of the track - discarded the partial extraction (5649 bytes) at /config/cache/subsync/shared/9f1e5589be7d7400/subtitle_4.srt
      +    at Jellyfin.Plugin.SubSync.Services.SubSyncService.ExtractSubtitleWithProgressAsync(String videoPath, Int32 streamIndex, String outputPath, Double durationSeconds, SyncJob job, CancellationToken cancellationToken)

11845 | 2026-09-19 00:14:26.622Z ERROR job 11f6074b7dd5453991049730e35fed7f failed: mode=ultimate item=6df9163a-04df-6beb-239a-032754411577 stream=4
      + System.InvalidOperationException: Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE': the Jellyfin user has no write permission there (the folder may also be mounted read-only). Nothing was changed and the original subtitle is untouched. Fix the folder's permissions for the Jellyfin user (or how the library is mounted) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.
      +    at Jellyfin.Plugin.SubSync.Services.SyncedTargetNaming.RequireWritable(String directory)

11875 | 2026-09-19 00:14:30.151Z ERROR job cc5b5af77ffd44bcb105f1791f75bf7f failed: mode=ultimate item=6df9163a-04df-6beb-239a-032754411577 stream=35
      + System.InvalidOperationException: Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE': the Jellyfin user has no write permission there (the folder may also be mounted read-only). Nothing was changed and the original subtitle is untouched. Fix the folder's permissions for the Jellyfin user (or how the library is mounted) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.
      +    at Jellyfin.Plugin.SubSync.Services.SyncedTargetNaming.RequireWritable(String directory)

11907 | 2026-09-19 00:14:35.797Z ERROR job 58f8e83190e24acbb9318631ef1d66d5 failed: mode=ultimate item=3a7b4d68-7958-7911-b227-fe8c7d3b6402 stream=4
      + System.InvalidOperationException: Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE': the Jellyfin user has no write permission there (the folder may also be mounted read-only). Nothing was changed and the original subtitle is untouched. Fix the folder's permissions for the Jellyfin user (or how the library is mounted) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.
      +    at Jellyfin.Plugin.SubSync.Services.SyncedTargetNaming.RequireWritable(String directory)

11941 | 2026-09-19 00:14:40.208Z ERROR job 3ae24e9c99ff4aa292198a9e38eaf3d5 failed: mode=ultimate item=3a7b4d68-7958-7911-b227-fe8c7d3b6402 stream=35
      + System.InvalidOperationException: Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE': the Jellyfin user has no write permission there (the folder may also be mounted read-only). Nothing was changed and the original subtitle is untouched. Fix the folder's permissions for the Jellyfin user (or how the library is mounted) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.
      +    at Jellyfin.Plugin.SubSync.Services.SyncedTargetNaming.RequireWritable(String directory)

12008 | 2026-09-19 00:14:54.757Z ERROR job 147e387756c642adb425a14d6c2751c8 failed: mode=ultimate item=67f0b7bb-51bf-8aa7-c9ba-4e35acc938e1 stream=35
      + System.InvalidOperationException: Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE': the Jellyfin user has no write permission there (the folder may also be mounted read-only). Nothing was changed and the original subtitle is untouched. Fix the folder's permissions for the Jellyfin user (or how the library is mounted) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.ed) and run again.
      +    at Jellyfin.Plugin.SubSync.Services.SyncedTargetNaming.RequireWritable(String directory)

12101 | 2026-09-19 00:15:28.335Z ERROR job d03e83e92e0845e28e68120657032e90 failed: mode=ultimate item=79fc02f2-4065-7ed3-a560-a79aec15286e stream=0
      + System.IO.IOException: The process cannot access the file '/media/synology2/Syn2-Serier/Arcane (2021) S01-S02 (1080p BluRay x265 10bit EAC3 Mixed Ghost)/Arcane (2021) S02/Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).SYNCED.srt' because it is being used by another process.
      +    at Microsoft.Win32.SafeHandles.SafeFileHandle.Open(String fullPath, FileMode mode, FileAccess access, FileShare share, FileOptions options, Int64 preallocationSize, UnixFileMode openPermissions, Int64& fileLength, UnixFileMode& filePermi

```

Each ERROR line is preceded by nothing but a job and is followed by its exception and frames; those 10 events own all 41 header-less continuation lines in the file. The distinct exception heads are:

```
System.IO.IOException: The process cannot access the file '/media/synology2/Syn2-Serier/Arcane (2021) S01-S02 (1080p BluRay x265 10bit EAC3 Mixed Ghost)/Arcane (2021) S02
System.InvalidOperationException: Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE': t
System.InvalidOperationException: ffmpeg subtitle extraction failed: ffmpeg read a partial file - it reported 'ended prematurely' - so the 81 cue(s) it produced are only 
System.InvalidOperationException: ffmpeg subtitle extraction failed: ffmpeg read a partial file - it reported 'ended prematurely' - so the 85 cue(s) it produced are only 
```

Reading of the split: 7 of the 10 ERROR lines are the *same* failure — a library folder the Jellyfin user cannot write (`Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE'`), hit twice at 11:57 and five times around 00:14–00:15; 2 are the ffmpeg partial-extraction failure on one file (`Thunder in My Heart - S01E03`), and 1 is a file-in-use error when replacing an Arcane sidecar (`System.IO.IOException: The process cannot access the file …`).

## [bound, not verified] markers

**10 occurrences**, at lines [225, 577, 1448, 3447, 7609, 7761, 8047, 9396, 9508, 12669].

The grep that proves the count (the literal, brackets escaped):

```
$ grep -n '\[bound, not verified\]' /tmp/logwork/snapshot.log
2026-09-18 03:30:06.827Z INFO  extract plan: Coherence.2013.1080p.BluRay.x265.10bit.DTS-WiKi.mkv cluster-walk [bound, not verified] expected 6587.41 MB/393 read(s) (15503.3 ms), actual 1754.77 MB/428411 read(s) (8257.6 ms) - bytes 0.27x, reads 1090.10x
2026-09-18 03:31:04.241Z INFO  extract plan: Coherence.2013.1080p.BluRay.x265.10bit.DTS-WiKi.mkv cluster-walk [bound, not verified] expected 6587.41 MB/393 read(s) (7480.2 ms), actual 1754.77 MB/428411 read(s) (3984.2 ms) - bytes 0.27x, reads 1090.10x
2026-09-18 03:32:29.283Z INFO  extract plan: The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 2000.08 MB/120 read(s) (1343.2 ms), actual 245.04 MB/59823 read(s) (329.0 ms) - bytes 0.12x, reads 498.53x
2026-09-18 11:46:12.429Z INFO  extract plan: The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 2000.08 MB/120 read(s) (1202.7 ms), actual 245.04 MB/59823 read(s) (294.6 ms) - bytes 0.12x, reads 498.53x
2026-09-18 12:12:22.982Z INFO  extract plan: Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 1085.33 MB/65 read(s) (56966.4 ms), actual 0.81 MB/197 read(s) (84.7 ms) - bytes 0.00x, reads 3.03x
2026-09-18 12:12:48.722Z INFO  extract plan: Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 1085.33 MB/65 read(s) (380590.2 ms), actual 0.81 MB/197 read(s) (565.8 ms) - bytes 0.00x, reads 3.03x
2026-09-18 12:13:21.501Z INFO  extract plan: The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 2000.08 MB/120 read(s) (2252.8 ms), actual 245.04 MB/59823 read(s) (551.9 ms) - bytes 0.12x, reads 498.53x
2026-09-18 14:17:31.820Z INFO  extract plan: Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 1085.33 MB/65 read(s) (2472890.4 ms), actual 0.81 MB/197 read(s) (3676.2 ms) - bytes 0.00x, reads 3.03x
2026-09-18 14:18:07.398Z INFO  extract plan: Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 1085.33 MB/65 read(s) (5896698.0 ms), actual 0.81 MB/197 read(s) (8765.9 ms) - bytes 0.00x, reads 3.03x
2026-09-19 00:17:46.161Z INFO  extract plan: The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 2000.08 MB/120 read(s) (1392.0 ms), actual 245.04 MB/59823 read(s) (341.0 ms) - bytes 0.12x, reads 498.53x
$ grep -c '\[bound, not verified\]' /tmp/logwork/snapshot.log
10
```

Note on the "surrounding job id" asked for: **these lines carry no job id at all** — `has_job_id_in_line` is `false` for all of them. They are file-level `extract plan:` lines (the plan that is an upper bound rather than an estimate), so the nearest job is not part of the record and cannot be attributed from the log without guessing. The files they belong to (3) are what identifies them, and the job ids of those files' jobs can be found from the `queued: job=… video=<that file>` lines. The 10 lines verbatim:

```
  225 | 2026-09-18 03:30:06.827Z INFO  extract plan: Coherence.2013.1080p.BluRay.x265.10bit.DTS-WiKi.mkv cluster-walk [bound, not verified] expected 6587.41 MB/393 read(s) (15503.3 ms), actual 1754.77 MB/428411 read(s) (8257.6 ms) - bytes 0.27x, reads 1090.10x
  577 | 2026-09-18 03:31:04.241Z INFO  extract plan: Coherence.2013.1080p.BluRay.x265.10bit.DTS-WiKi.mkv cluster-walk [bound, not verified] expected 6587.41 MB/393 read(s) (7480.2 ms), actual 1754.77 MB/428411 read(s) (3984.2 ms) - bytes 0.27x, reads 1090.10x
 1448 | 2026-09-18 03:32:29.283Z INFO  extract plan: The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 2000.08 MB/120 read(s) (1343.2 ms), actual 245.04 MB/59823 read(s) (329.0 ms) - bytes 0.12x, reads 498.53x
 3447 | 2026-09-18 11:46:12.429Z INFO  extract plan: The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 2000.08 MB/120 read(s) (1202.7 ms), actual 245.04 MB/59823 read(s) (294.6 ms) - bytes 0.12x, reads 498.53x
 7609 | 2026-09-18 12:12:22.982Z INFO  extract plan: Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 1085.33 MB/65 read(s) (56966.4 ms), actual 0.81 MB/197 read(s) (84.7 ms) - bytes 0.00x, reads 3.03x
 7761 | 2026-09-18 12:12:48.722Z INFO  extract plan: Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 1085.33 MB/65 read(s) (380590.2 ms), actual 0.81 MB/197 read(s) (565.8 ms) - bytes 0.00x, reads 3.03x
 8047 | 2026-09-18 12:13:21.501Z INFO  extract plan: The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 2000.08 MB/120 read(s) (2252.8 ms), actual 245.04 MB/59823 read(s) (551.9 ms) - bytes 0.12x, reads 498.53x
 9396 | 2026-09-18 14:17:31.820Z INFO  extract plan: Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 1085.33 MB/65 read(s) (2472890.4 ms), actual 0.81 MB/197 read(s) (3676.2 ms) - bytes 0.00x, reads 3.03x
 9508 | 2026-09-18 14:18:07.398Z INFO  extract plan: Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 1085.33 MB/65 read(s) (5896698.0 ms), actual 0.81 MB/197 read(s) (8765.9 ms) - bytes 0.00x, reads 3.03x
12669 | 2026-09-19 00:17:46.161Z INFO  extract plan: The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv cluster-walk [bound, not verified] expected 2000.08 MB/120 read(s) (1392.0 ms), actual 245.04 MB/59823 read(s) (341.0 ms) - bytes 0.12x, reads 498.53x
```

Distinct files carrying the marker: `Coherence.2013.1080p.BluRay.x265.10bit.DTS-WiKi.mkv`, `The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv`, `Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv`

## Read / byte counts

### `this walk moved … MB … in … s = … MB/s`

There are **17** of these lines. Every value, verbatim as a table (line, job, MB, s, MB/s):

| line | job | MB | s | MB/s |
|---|---|---|---|---|
| 999 | `6e1542fc99db428d92edbb3e7ce55297` | 6662.9 | 54.5 | 128.3 |
| 2269 | `7fe98ec4aed34ffe84de9316c37dd9a4` | 13087.1 | 1054.2 | 13.0 |
| 1549 | `1ba973153d054a30b50329bf8d78ef18` | 1907.4 | 10.6 | 188.0 |
| 2099 | `52447ad90bf440819dae0e5cfef29e27` | 8142.4 | 695.2 | 12.3 |
| 2051 | `9e9b13c817b54e93846655f042577eda` | 2631.6 | 2.0 | 1377.0 |
| 2498 | `1716a26c5f6e4411bba5776675ef8a0e` | 8562.3 | 334.6 | 26.8 |
| 6521 | `3232d380462643f483af51b33f112399` | 2631.6 | 109.9 | 25.1 |
| 7163 | `f49c4b25cb0b407b85bdeaffe367a040` | 8167.7 | 113.1 | 75.8 |
| 3512 | `7f0b9ce247214e7d9b5612eeca9ca2fb` | 1362.5 | 34.0 | 42.0 |
| 3516 | `f370e7494ed545678f62e80bff42ac2c` | 1907.4 | 14.7 | 136.3 |
| 8057 | `ad0e637649054684b02254de49364e5a` | 1907.4 | 1.3 | 1561.3 |
| 9594 | `658cf5435ae140358e4e54f014b0aa2b` | 8483.6 | 58.7 | 151.5 |
| 9604 | `5b5ffb09489449f68f0cf2ee380b98fc` | 8483.6 | 3.4 | 314.1 |
| 12222 | `1170914f61bd4c0b8b1ca549d694db54` | 2631.6 | 6.3 | 440.3 |
| 12262 | `0873927ae4184fcc868b541d17e07669` | 2631.6 | 6.5 | 425.9 |
| 12098 | `7e13311e4e214c9a95c1aedcced2e987` | 1362.5 | 40.1 | 35.6 |
| 12739 | `1bc9cec0d1624148aeef43a1428f05c2` | 1907.4 | 10.2 | 195.3 |

Family definition: these numbers are self-annotated as *not* storage measurements (every line ends "… but it is not being used to judge that volume: N job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage"). The family is therefore described in two ways: the MB moved per walk and the implied MB/s.
- **MB per walk**: min **1362.5**, max **13087.1**, median 2631.6; Tukey fences (1,5×IQR) at -7483.0 and 17558.1 → **no outliers**.
- **MB/s**: min **12.3**, max **1561.3**, median 136.3; Tukey fences at -382.1 and 731.9 → **2 outliers**: [1377.0, 1561.3].

The two MB/s outliers are lines 2051 (job `9e9b13c817b54e93846655f042577eda`, 2631.6 MB in 2.0 s = 1377.0 MB/s) and 8057 (job `ad0e637649054684b02254de49364e5a`, 1907.4 MB in 1.3 s = 1561.3 MB/s) — both are partial-file walks that stopped early, which is exactly why the line refuses to be a storage verdict. The same file walked twice shows what that means: `False Trail (2011) Bluray-1080p.mkv` appears at line 9604 as 8483.6 MB in 3.4 s = 314.1 MB/s and at line 9594 as 8483.6 MB in 58.7 s = 151.5 MB/s — the same bytes, two moments, two answers.

### `bytes=` on completion lines

**976** completion lines carry a `bytes=` field: **791 are `bytes=unknown`** and **185 are a number**.

Family definition: the family is the set of 185 numeric values, tested with the Tukey 1,5×IQR rule on the values themselves — min **61**, max **110828**, Q1 26915.0, median 35330, Q3 42042.0, so the fences are 4224.5 and 64732.5.

**20 values fall outside the family.** The low group is 7 values — 61 (×2), 98, 116, 124, 150, 367 bytes. The high group is 13 values — 65 002 (×2), 67 461 (×2), 67 472, 73 010 (×2), 78 005, 78 117, 80 241 (×2), 99 583, 110 828 bytes.

There are **162 distinct** byte values among the 185; the full per-job list is the JSON key `read_byte_counts.bytes_values` (line, job, bytes) so it can be checked mechanically. The 791 `bytes=unknown` rows are all completion lines that wrote nothing, except 16 of them — see the negative-score finding below.

### `extract plan:` expected-vs-actual (the read accounting the extraction itself prints)

**879** such lines. For the 879 that carry an expected/actual pair: expected MB min 0.0 / max 6587.41, median 1.09; actual MB min 0.0 / max 1754.77, median 1.08; the bytes ratio is 1,00x at the median and has 40 outliers, which are the two clusters below.
- **over-read cluster (the Solsidan S03 Blu-ray set):** bytes 60,87x–71,93x, e.g. line 7030 `extract plan: solsidan.s03e03.swedish.1080p.bluray.x264-prince.mkv cue-indexed expected 0.62 MB/518 read(s) (102909.8 ms), actual 38.88 MB/9861 read(s) (2980720.3 ms) - bytes 63.20x, reads 19.04x`
- **under-read cluster (the two `cluster-walk [bound, not verified]` files):** bytes 0,12x/0,27x — Coherence 2013 (line 225, expected 6587.41 MB/393 read(s) against actual 1754.77 MB/428411 read(s)) and The Trio S01E04 (line 1448, 2000.08 MB → 245.04 MB). These carry the marker in section 4 and are the same plans: a bound is not a prediction.

### Other read-accounting lines that are not `bytes=`

- `extract lane: <file> -> N/N subtitle(s), X MB, N reads, N ms, ok=…` — **427** summaries: **424 report `ok=True`** and **3 report `ok=False`** (638, 7611, 9398). Plus **9** `extract lane: started` and **6** `extract lane: stopped` lines (442 `extract lane:` lines in all). The 3 `ok=False` lines are the extraction failures the ERROR lines above are the jobs of: `The Lighthouse (2019) Bluray-1080p.mkv -> 0/2 subtitle(s) … ok=False, reason=unsupported block encoding` (line 638), and `Thunder in My Heart - S01E03 - The Triangle of Conscience WEBDL-1080p.mkv -> 0/2 subtitle(s), 0,8 MB, 209 reads … ok=False, reason=no subtitle blocks found` twice (lines 7611, 9398).
- `walk ceiling: holding <volume> at N concurrent media read(s) …` — **147** lines, over the volumes `/Media|/dev/nvme0n1p2`, `/media/synology2|192.168.0.110:/volume2/VOLYM\0402`, `/media/synology|192.168.0.110:/volume1/JELLYFIN`, `/media|/dev/sda2`, of which **48** say the volume "is thrashing".
- `queue lock slow: holder=… ms=…` — **167** lines, max **30483 ms**, 50 of them over 1 000 ms.

## Sync outcomes

```
$ grep -c 'queued: job=' /tmp/logwork/snapshot.log
1268
$ grep -oE 'job [0-9a-f]{32} (completed|UNVERIFIED|REFUSED|failed)' /tmp/logwork/snapshot.log | awk '{print $3}' | sort | uniq -c
    976 completed
     14 UNVERIFIED
     10 failed
      1 REFUSED
$ grep -c 'Done (' /tmp/logwork/snapshot.log
0
$ grep -icE '\bCancelled\b|killed by the user|stopped \(' /tmp/logwork/snapshot.log
0
```

| outcome | jobs |
|---|---|
| `queued: job=…` lines | 1268 |
| completed (`job <id> completed:`) | **976** — mode=ultimate 974, mode=normal 2 |
| of those, already in sync (nothing written) | 791 |
| of those, a file was written | **185** |
| UNVERIFIED (nothing written) | 14 |
| REFUSED (nothing written) | 1 |
| failed (ERROR) | 10 |
| cancelled | **0** |
| engine runs (`ffsubsync start:` / `ffsubsync exit=`) | 1000 / 1000 |

Failure and refusal text, verbatim. The single refusal (line 8476):

```
2026-09-18 12:17:52.833Z INFO  job 656525ac4479490491686e2dd7a535d7 REFUSED: this subtitle is further out than the plugin is searching (the 300 s window also reached its limit (519020 ms)); raise "Maximum offset" and run it again, or sync it by hand. Nothing written, source untouched, file=/media/synology/Syn-Filmer/Himlen är oskyldigt blå (2010)/Himlen är oskyldigt blå (2010).H.264.Prime-WEB-DL.mkv
```
Its reason field, exactly: `this subtitle is further out than the plugin is searching (the 300 s window also reached its limit (519020 ms)); raise "Maximum offset" and run it again, or sync it by hand. Nothing written, source untouched, file=/media/synology/Syn-Filmer/Himlen är oskyldigt blå (2010)/Himlen är oskyldigt blå (2010).H.264.Prime-WEB-DL.mkv`

All 14 UNVERIFIED lines share one reason shape — "the audio was the only ruler (… ms offset) and this track has no reference subtitle to check it against; nothing written, source untouched" — with the offset differing; the first of them, verbatim (line 1004):

```
2026-09-18 03:31:55.872Z INFO  job 6e1542fc99db428d92edbb3e7ce55297 UNVERIFIED: the audio was the only ruler (+90 ms offset) and this track has no reference subtitle to check it against; nothing written, source untouched, file=/Media/Movies/Min lilla syster/Min.lilla.syster.2015.1080p.BluRay.DD5.1..x264-NDF.mkv
```
The 14 UNVERIFIED offsets are: +90 ms, +50 ms, -30 ms, -10 ms, -10 ms, +80 ms, -20 ms, -70 ms, -10 ms, +20 ms, +50 ms, -70 ms, -30 ms, -10 ms.

All 10 failed lines are quoted verbatim in the WARN/ERROR section above; a failed job gets no `completed:` line at all, so it writes nothing — the exception each one raises is a write or extraction refusal (`Cannot write the synced subtitle to …`, `ffmpeg subtitle extraction failed …`, `The process cannot access the file …`).

### Per-batch totals

There are **no `Done (...)`-style lines** in this log: `grep -c 'Done ('` is 0, and a case-insensitive search for batch-completion wording (`batch … (done|finished|complete)`) is also 0. What the log does carry per batch is the queue line and the per-job tallies. The three queue lines, verbatim:

```
3000 | 2026-09-18 11:44:13.293Z INFO  batch b76b77e3edb5450999bc5d8e207cb679 queued: tasks=459 mode=auto label='Selection (285 files)' totalMs=1592 slowestTaskMs=233 (index 176: gap=−2 ms; item=0 ms, sources=2 ms, settings=0 ms, log=0 ms, total=233 ms)
9119 | 2026-09-18 14:16:13.438Z INFO  batch cd29cd12a01e4be89018d3b9ae6751b8 queued: tasks=35 mode=auto label='Selection (285 files)' totalMs=166 slowestTaskMs=36 (index 13: gap=−3 ms; item=0 ms, sources=2 ms, settings=0 ms, log=0 ms, total=36 ms)
10405 | 2026-09-19 00:10:12.715Z INFO  batch 8b8dd2d6274c4d3187c62b887b0170a9 queued: tasks=772 mode=auto label='Selection (285 files)' totalMs=2455 slowestTaskMs=245 (index 188: gap=−2 ms; item=0 ms, sources=2 ms, settings=0 ms, log=0 ms, total=245 ms)
```

Reconciling those `tasks=` numbers against the jobs, by batch id:

| batch | queued `tasks=` | completed | failed | UNVERIFIED | REFUSED | no terminal line | tasks seen in this file | files | first event | last event |
|---|---|---|---|---|---|---|---|---|---|---|
| `b76b77e3edb5450999bc5d8e207cb679` | 459 | 452 | 3 | 3 | 1 | 0 | 459 | 298 | 2026-09-18 11:44:11.727 | 2026-09-18 12:26:11.540 |
| `cd29cd12a01e4be89018d3b9ae6751b8` | 35 | 34 | 1 | 0 | 0 | 0 | 35 | 35 | 2026-09-18 14:16:13.299 | 2026-09-18 14:19:48.006 |
| `(standalone)` | (standalone — no `batch … queued` line) | 2 | 0 | 0 | 0 | 0 | 2 | 1 | 2026-09-18 14:21:36.121 | 2026-09-18 14:25:25.374 |
| `8b8dd2d6274c4d3187c62b887b0170a9` | 772 | 270 | 6 | 3 | 0 | 493 | 772 | 339 | 2026-09-19 00:10:10.286 | 2026-09-19 00:20:12.757 |

Every queued task of the two batches that finished has a terminal line — 459/459 for `b76b77e3…` and 35/35 for `cd29cd12…`; nothing went missing. The third batch, `8b8dd2d6274c4d3187c62b887b0170a9`, was **still running when the snapshot ended**: 493 of its 772 tasks had no terminal line, all queued at 00:10:10–00:10:12Z, and the last `queue:` line (line 13357, 2026-09-19 00:20:13.234Z) still reports `488 queued, 5 running`. This is an unfinished run, not a lost one.

### Run accounting across the file

| item | count |
|---|---|
| distinct job ids seen | 1494 |
| job ids with a `queued:` line here | 1268 |
| job ids with a terminal line | 1001 |
| queued here with **no** terminal line | **493** — all in batch `8b8dd2d6…`, queued 2026-09-19 00:10:10.724Z–2026-09-19 00:10:12.715Z |
| terminal line with **no** `queued:` line here (queued in the previous file) | 226 — 2026-09-18 03:29:27.364Z–2026-09-18 03:58:59.874Z |
| engine ran, never exited | 1 |
| engine ran, no terminal line | 5 |
| duplicate `queued:` job ids | 0 |
| jobs that ran the engine twice | 0 |

The 5 engine runs with no terminal line are **not** vanished runs: all five start within the last 5½ minutes of the snapshot and four of them have no `exit=` either, i.e. they were still in the ffsubsync process when the snapshot was taken:

| job | start timestamp | file |
|---|---|---|
| `5e1a5cf93a7441c4a0aaa05fc0284c4d` | 2026-09-19 00:15:13.823Z | `/media/Serier/Black Mirror/Black.Mirror.2011.S06.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE/Black.Mirror.2011.S06E02.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv` |
| `2cc79059a7614e63b850e9dee723dc3a` | 2026-09-19 00:16:23.852Z | `/media/Serier/Black Mirror/Black.Mirror.2011.S06.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE/Black.Mirror.2011.S06E05.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv` |
| `24149a50bc784118b95a3334f1df5073` | 2026-09-19 00:19:48.359Z | `/media/synology/Syn-Serier/The Helicopter Heist/Season 1/The Helicopter Heist - S01E02 - The White Whale WEBDL-1080p.mkv` |
| `5b21e4da6ae5425e9ad65ae920b14f7f` | 2026-09-19 00:19:38.337Z | `/media/synology2/Syn2-Filmer/The Ugly Stepsister (2025)/The Ugly Stepsister (2025) Bluray-1080p.mkv` |
| `adcf03b74fec4cb1af1869878d567973` | 2026-09-19 00:20:10.902Z | `/media/synology2/Syn2-Serier/Wake/Season 1/Wake - S01E03 - Thicker Than Water WEBDL-1080p.mkv` |

(The fifth, `adcf03b7…`, has an `exit=0` and an alignment at 00:20:12.757Z but no completion line before the snapshot ends — it was mid-job. What would settle whether any of them ever failed: the next tail of the live log.)

## Suspicious offsets and patterns

Method, so every flag is reproducible: a job's **align offset** is the `offset=` on its last `ffsubsync alignment:` line (the engine's answer, in seconds); its **written offset** is the ms value inside `change=…` on its completion line (what the user actually got). 1041 alignment lines were parsed for 1002 jobs. Align offsets run from -149.99 s to 148.51 s, and 786 of them are exactly 0,000 s — the day is overwhelmingly a "confirm it is already in sync" day.

| flag | count | table |
|---|---|---|
| align offset > +5 s or < −5 s | **53** (50 of them wrote a file) | 7.1 |
| align offset negative | **103** | 7.2 |
| union of the two align flags | **130** | 7.1/7.2 |
| written offset > +5 000 ms or < −5 000 ms | **31** | 7.3 |
| written offset negative | **86** | 7.4 |
| union of the two written flags | **94** | 7.3/7.4 |
| engine score a Tukey outlier | **71** of 1002 scored jobs | 7.5 |
| engine score negative | **13** | 7.6 |
| jobs whose **last** alignment is negative — **11** of the 13 wrote a file** | 13 | 7.7 |
| jobs with a framerate/rescale line | **643** — 472 rescale, 171 declined, ratios 0,90x–2,40x | 7.8 |

### 7.1 Jobs with |align offset| > 5 s (53 jobs)

| job | file | offset (s) | score | wrote a file? | written offset (ms) | why flagged |
|---|---|---|---|---|---|---|
| `c35bf99dbbb54cc8be0b2a79b0c7e36d` | The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv | -149.99 | -14333.68 | yes | — | offset pinned at the ±150 s window bound; **negative score**; `change=` reports no offset |
| `3a8fbe3d24194ae9bf444ff3064beb65` | Solsidan.S09E01.mkv | -149.99 | -41642.0 | yes | — | offset pinned at the ±150 s window bound; **negative score**; `change=` reports no offset |
| `6f32e35558f3411b961ee34d931e0899` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -144.38 | -41855.162 | yes | — | offset pinned at the ±150 s window bound; **negative score**; `change=` reports no offset |
| `155dd60e4ded4ed287bd7da861cb4081` | Solsidan.S09E03.mkv | -143.8 | -60411.0 | yes | — | offset pinned at the ±150 s window bound; **negative score**; `change=` reports no offset |
| `0e1392c9c28d41a2b9eec230fd371526` | The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv | -139.18 | -2685.04 | yes | — | large shift; **negative score**; `change=` reports no offset |
| `c5fe02743cf14a689c8503de0511bff8` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -137.84 | -29597.32 | yes | — | large shift; **negative score**; `change=` reports no offset |
| `6840e36108a74f34ad06a0658e2f2b2a` | The Helicopter Heist - S01E02 - The White Whale WEBDL-1080p.mkv | -131.74 | 17606.858 | yes | — | large shift; `change=` reports no offset |
| `b92d2470a0344d6fb25327c6e2a165d1` | The Helicopter Heist - S01E02 - The White Whale WEBDL-1080p.mkv | -131.74 | 17606.858 | yes | — | large shift; `change=` reports no offset |
| `14c87ee5a5ec46c29ed6f87db09cea4b` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -123.85 | -36522.0 | yes | — | large shift; **negative score**; `change=` reports no offset |
| `a38c80abd34345d0aeeb73b58132d97c` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -123.85 | -36522.0 | yes | — | large shift; **negative score**; `change=` reports no offset |
| `ba3b459ab20b47448296c0b4d8c0525f` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -123.74 | -37612.56 | yes | — | large shift; **negative score**; `change=` reports no offset |
| `89dec4ae6a514918b03a8755e582fdaf` | The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv | -121.88 | -3481.721 | yes | — | large shift; **negative score**; `change=` reports no offset |
| `d4d768364d14408a8e8f5650fa7dba26` | The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv | -121.88 | -3481.721 | yes | — | large shift; **negative score**; `change=` reports no offset |
| `ebf91dc1bda04500bbe3e02a17b1e71d` | Vargasommar S01E04.mkv | -101.66 | 149871.432 | yes | -56035 | large shift |
| `396722f3e44549dcb779c7562ec08194` | The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv | -57.54 | 62077.0 | yes | -57540 | large shift |
| `ef0df6046cf84a24b29238800e58db83` | The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv | -57.45 | 59996.0 | yes | -57450 | large shift |
| `20e719c4973541649f28cb5bf9405e34` | The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv | -44.58 | 20268.0 | yes | — | large shift; `change=` reports no offset |
| `c0dbbbd9539c45c0a08ba44c49ec4e77` | American.Nightmare.2024.S01E02.1080p.WEB.h264-GP-TV-NLsubs.mkv | -39.94 | 13165.0 | yes | — | large shift; `change=` reports no offset |
| `cce71995f68c43afaf7d7ad7d2af0802` | American.Nightmare.2024.S01E02.1080p.WEB.h264-GP-TV-NLsubs.mkv | -39.94 | 13165.0 | yes | — | large shift; `change=` reports no offset |
| `66110847a9154b4cb89504c640dc83ad` | The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv | -28.54 | 39751.0 | yes | -86300 | large shift |
| `5a1e258abf5548edbab82b65b80b6112` | The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv | -28.54 | 39751.0 | yes | -86300 | large shift |
| `f62f616e395241ebaafd9f2dfd5b710c` | The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv | -22.86 | 60411.6 | yes | — | large shift; `change=` reports no offset |
| `2cc79059a7614e63b850e9dee723dc3a` | Black.Mirror.2011.S06E05.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv | -15.76 | 333534.0 | no | — | large shift |
| `72744fabf04e4816a095b1d93cfb1344` | The Helicopter Heist - S01E07 - The Hunt WEBDL-1080p.mkv | -5.24 | 80654.0 | yes | -58373 | large shift |
| `ffb11b7199274641960943e07ba4d310` | The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv | -5.15 | 139492.36 | yes | -40 | large shift |
| `aa6a8a893e754d90b9b306e443e34508` | The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv | -5.12 | 139684.36 | yes | -10 | large shift |
| `08fed8a2e279470196ab726640ad6730` | Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv | 11.97 | 116311.0 | yes | -23196 | large shift |
| `2ad489446ca74426abb8706984658031` | Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv | 12.04 | 116523.0 | yes | -23017 | large shift |
| `ed2aec15ed14488eb3ddaa854a1f4837` | Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv | 12.04 | 116523.0 | yes | -23017 | large shift |
| `f370e7494ed545678f62e80bff42ac2c` | The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv | 20.03 | 69326.0 | yes | -13471 | large shift |
| `1bc9cec0d1624148aeef43a1428f05c2` | The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv | 20.03 | 69326.0 | yes | -13471 | large shift |
| `b031527d3f55499e9f61944d0cf40292` | The Helicopter Heist - S01E05 - Making Tsunami WEBDL-1080p.mkv | 20.45 | 113111.08 | yes | 60274 | large shift |
| `1170914f61bd4c0b8b1ca549d694db54` | The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv | 21.93 | 132257.0 | yes | -22988 | large shift |
| `f094db5567ec4681b3494f057698cd44` | Gosta.s01e10.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | 27.72 | 45627.0 | yes | -5571 | large shift |
| `f35acd4f02df42bba6fdbdee665ae4ad` | Gosta.s01e11.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | 33.29 | 45751.0 | yes | -1793 | large shift |
| `63c9d45facf344499675c4e92fa4e23b` | The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv | 42.03 | 95522.0 | yes | -15769 | large shift |
| `871ff2acb6f2431e85375f04110c98c7` | The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv | 42.03 | 95522.0 | yes | -15769 | large shift |
| `cb0dd08064b249f8b741baa6fc7005a0` | The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv | 42.33 | 99634.0 | yes | -14337 | large shift |
| `ff1c2f6564164698836fe65d09cebfc7` | The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv | 42.67 | 85322.0 | yes | -14686 | large shift |
| `3e615acc7d1c4f37aa1277d9636f2447` | Gosta.s01e02.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | 44.92 | 31640.0 | yes | 13078 | large shift |
| `1037b11ac97d468d99e7aa484983b370` | Gosta.s01e09.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | 50.03 | 47940.0 | yes | 21831 | large shift |
| `e71951516d8c408db61a292baa34feea` | The Helicopter Heist - S01E08 - Too Close to the Sun WEBDL-1080p.mkv | 56.9 | 63975.965 | yes | 98693 | large shift |
| `a4b96e714c324f4cbca7d45997bf0319` | Wake - S01E03 - Thicker Than Water WEBDL-1080p.mkv | 57.18 | 112255.0 | yes | 7735 | large shift |
| `adcf03b74fec4cb1af1869878d567973` | Wake - S01E03 - Thicker Than Water WEBDL-1080p.mkv | 57.19 | 200217.0 | no | — | large shift |
| `dacafdca51124b87b1c837c1f249100e` | Tell.Me.Who.I.Am.2019.1080p.NF.WEB-DL.DDP5.1.X264-IKA-Obfuscated.mkv | 98.41 | 3220.0 | yes | — | large shift; `change=` reports no offset |
| `0457506bd37e4aa5b45b3d7c03376281` | Tell.Me.Who.I.Am.2019.1080p.NF.WEB-DL.DDP5.1.X264-IKA-Obfuscated.mkv | 98.41 | 3220.0 | yes | — | large shift; `change=` reports no offset |
| `c7336759e2864ef2a38c2504fa2341d5` | Farsan (2010).H.264.Prime-WEB-DL.mkv | 139.05 | 731.646 | yes | 139106 | large shift |
| `45d8d614ed124e59a07c6017bde287a2` | Farsan (2010).H.264.Prime-WEB-DL.mkv | 139.05 | 731.646 | yes | 139106 | large shift |
| `656525ac4479490491686e2dd7a535d7` | Himlen är oskyldigt blå (2010).H.264.Prime-WEB-DL.mkv | 139.17 | 241063.457 | no | — | large shift |
| `a45ceccdbd034825af238c25642bbd13` | Vargasommar S01E05.mkv | 141.49 | 59261.72 | yes | — | offset pinned at the ±150 s window bound; `change=` reports no offset |
| `e401ab29927246628abbbb7bfb594448` | The Puppet Master - Hunting the Ultimate Conman - S01E03 - Setting the Trap WEBDL-1080p.mkv | 145.59 | 15.0 | yes | — | offset pinned at the ±150 s window bound; `change=` reports no offset |
| `29abd22dba984f1b9502ff83da3e9e83` | The Puppet Master - Hunting the Ultimate Conman - S01E03 - Setting the Trap WEBDL-1080p.mkv | 145.59 | 15.0 | yes | — | offset pinned at the ±150 s window bound; `change=` reports no offset |
| `ad8cea7b036744218b6c436ec9191e06` | BoJack Horseman (2014) - S01E02 - BoJack Hates the Troops (1080p BluRay x265 Ghost).mkv | 148.51 | 25051.16 | yes | 167533 | offset pinned at the ±150 s window bound |

### 7.2 Jobs with a negative align offset (103 jobs)

```
 5763  c35bf99dbbb54cc8be0b2a79b0c7e36d  offset   -149.99 s  score   -14333.68  wrote=yes  The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv
 8695  3a8fbe3d24194ae9bf444ff3064beb65  offset   -149.99 s  score    -41642.0  wrote=yes  Solsidan.S09E01.mkv
 3631  6f32e35558f3411b961ee34d931e0899  offset   -144.38 s  score  -41855.162  wrote=yes  The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv
 8704  155dd60e4ded4ed287bd7da861cb4081  offset    -143.8 s  score    -60411.0  wrote=yes  Solsidan.S09E03.mkv
11397  0e1392c9c28d41a2b9eec230fd371526  offset   -139.18 s  score    -2685.04  wrote=yes  The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv
11973  c5fe02743cf14a689c8503de0511bff8  offset   -137.84 s  score   -29597.32  wrote=yes  The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv
 3850  6840e36108a74f34ad06a0658e2f2b2a  offset   -131.74 s  score   17606.858  wrote=yes  The Helicopter Heist - S01E02 - The White Whale WEBDL-1080p.mkv
13281  b92d2470a0344d6fb25327c6e2a165d1  offset   -131.74 s  score   17606.858  wrote=yes  The Helicopter Heist - S01E02 - The White Whale WEBDL-1080p.mkv
 3617  14c87ee5a5ec46c29ed6f87db09cea4b  offset   -123.85 s  score    -36522.0  wrote=yes  The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv
11958  a38c80abd34345d0aeeb73b58132d97c  offset   -123.85 s  score    -36522.0  wrote=yes  The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv
11929  ba3b459ab20b47448296c0b4d8c0525f  offset   -123.74 s  score   -37612.56  wrote=yes  The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv
 3559  89dec4ae6a514918b03a8755e582fdaf  offset   -121.88 s  score   -3481.721  wrote=yes  The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv
11350  d4d768364d14408a8e8f5650fa7dba26  offset   -121.88 s  score   -3481.721  wrote=yes  The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv
 8853  ebf91dc1bda04500bbe3e02a17b1e71d  offset   -101.66 s  score  149871.432  wrote=yes  Vargasommar S01E04.mkv
13196  396722f3e44549dcb779c7562ec08194  offset    -57.54 s  score     62077.0  wrote=yes  The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv
13243  ef0df6046cf84a24b29238800e58db83  offset    -57.45 s  score     59996.0  wrote=yes  The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv
11798  20e719c4973541649f28cb5bf9405e34  offset    -44.58 s  score     20268.0  wrote=yes  The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv
 3025  c0dbbbd9539c45c0a08ba44c49ec4e77  offset    -39.94 s  score     13165.0  wrote=yes  American.Nightmare.2024.S01E02.1080p.WEB.h264-GP-TV-NLsubs.mkv
10422  cce71995f68c43afaf7d7ad7d2af0802  offset    -39.94 s  score     13165.0  wrote=yes  American.Nightmare.2024.S01E02.1080p.WEB.h264-GP-TV-NLsubs.mkv
 3840  66110847a9154b4cb89504c640dc83ad  offset    -28.54 s  score     39751.0  wrote=yes  The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv
13262  5a1e258abf5548edbab82b65b80b6112  offset    -28.54 s  score     39751.0  wrote=yes  The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv
 5807  f62f616e395241ebaafd9f2dfd5b710c  offset    -22.86 s  score     60411.6  wrote=yes  The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv
12395  2cc79059a7614e63b850e9dee723dc3a  offset    -15.76 s  score    333534.0  wrote=no   Black.Mirror.2011.S06E05.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv
 4392  72744fabf04e4816a095b1d93cfb1344  offset     -5.24 s  score     80654.0  wrote=yes  The Helicopter Heist - S01E07 - The Hunt WEBDL-1080p.mkv
 9038  ffb11b7199274641960943e07ba4d310  offset     -5.15 s  score   139492.36  wrote=yes  The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv
 2456  aa6a8a893e754d90b9b306e443e34508  offset     -5.12 s  score   139684.36  wrote=yes  The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv
 9045  3b311a9838fc4e2f87560c8d5f161639  offset     -4.99 s  score   137929.48  wrote=yes  The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv
 9539  cf955867af634d4aa6b2414de2d472b9  offset     -4.99 s  score   137929.48  wrote=yes  The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv
 8899  af70e4d801cc4df6acecca7fdba8af57  offset     -2.74 s  score   153415.28  wrote=no   The White Lotus (2021) - S02E06 - Abductions (1080p HMAX WEB-DL x265 Ghost).mkv
 6913  3e9e9f5a844e44aba7e929b4127cb166  offset     -1.74 s  score   159816.44  wrote=yes  The Helicopter Heist - S01E07 - The Hunt WEBDL-1080p.mkv
 2084  0d8a3d7ed3c243f98c4d0d6cffd074fe  offset     -1.47 s  score   135149.72  wrote=yes  The Helicopter Heist - S01E03 - The Devil Is in the Details WEBDL-1080p.SYNCED.zho.srt
 3863  41dd60b84e7745e6b74f52633c543ad7  offset     -1.29 s  score   135825.56  wrote=yes  The Helicopter Heist - S01E03 - The Devil Is in the Details WEBDL-1080p.mkv
 4616  7232ae588a90457aa2ce400d2acf12e3  offset     -1.14 s  score    115673.0  wrote=yes  Gosta.s01e02.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv
 3036  5a5d416f43f144de8a94970812e3e092  offset     -0.85 s  score    173735.0  wrote=yes  Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv
10996  c6abafc1a5ff47289be2a935b2e1192f  offset     -0.85 s  score    173735.0  wrote=yes  Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv
10728  219458230e9741318ea426483ff29abb  offset     -0.83 s  score    155594.0  wrote=no   Gosta.s01e01.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv
12124  0ad9f1f11fa74ae4b9c5648f9f1f7689  offset     -0.58 s  score   148068.08  wrote=yes  Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
 3508  debd6b7dc1604e5baa0f1016910bf870  offset     -0.54 s  score   148768.88  wrote=yes  Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
11768  77db6d829fd846838f32aaa78ae3fc83  offset     -0.54 s  score   148768.88  wrote=yes  Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
 3595  ba63b38277df4581ab76b609af2fe945  offset     -0.53 s  score    154636.4  wrote=yes  Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
 9177  7c8a35f82ddd4392a2683288408d6896  offset     -0.53 s  score   153851.12  wrote=yes  Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
11728  d03e83e92e0845e28e68120657032e90  offset     -0.53 s  score   153622.64  wrote=no   Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
 4613  88260220823a406281a747c97a5c8013  offset     -0.51 s  score    155595.0  wrote=yes  Gosta.s01e01.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv
 4677  0820631bccf94143af388729238fb1bd  offset     -0.47 s  score    139595.0  wrote=yes  Gosta.s01e09.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv
 2245  e8572413da9c48dc97c21d3d59e9f638  offset     -0.34 s  score    224761.0  wrote=yes  The White Lotus (2021) - S02E01 - Ciao (1080p HMAX WEB-DL x265 Ghost).mkv
 2329  ea3ea265c9ff43c791a4e802051cf961  offset     -0.33 s  score    222936.0  wrote=yes  The White Lotus (2021) - S02E06 - Abductions (1080p HMAX WEB-DL x265 Ghost).mkv
  299  88d872c92271458ca018e851956f8499  offset     -0.29 s  score    130091.0  wrote=yes  BoJack Horseman (2014) - S03E04 - Fish Out Of Water (1080p NF WEB-DL x265 Ghost).mkv
 2283  4eb295c5d42647be94a3c093e00ecc54  offset     -0.24 s  score    247333.0  wrote=yes  The White Lotus (2021) - S02E03 - Bull Elephants (1080p HMAX WEB-DL x265 Ghost).mkv
 8812  2aae2b21091740219fce641694d4ff92  offset     -0.23 s  score    222245.0  wrote=yes  The White Lotus (2021) - S02E01 - Ciao (1080p HMAX WEB-DL x265 Ghost).mkv
 2163  85f136dedd0140e5a0b863020f554709  offset     -0.18 s  score   196837.88  wrote=yes  The Helicopter Heist - S01E07 - The Hunt WEBDL-1080p.mkv
 8829  43b60648eaa04c70a90733bc5d506773  offset     -0.18 s  score    227675.0  wrote=yes  The White Lotus (2021) - S02E02 - Italian Dream (1080p HMAX WEB-DL x265 Ghost).mkv
 8846  3c690b59cb0d41368e6566ec2e5a5f0b  offset     -0.18 s  score    240487.0  wrote=yes  The White Lotus (2021) - S02E03 - Bull Elephants (1080p HMAX WEB-DL x265 Ghost).mkv
 8871  0cce38be02114e139eac4fa4ac734a1b  offset     -0.18 s  score    188615.0  wrote=yes  The White Lotus (2021) - S02E04 - In the Sandbox (1080p HMAX WEB-DL x265 Ghost).mkv
 8882  9c90d7ef422c42099db4b77b3a6c49da  offset     -0.17 s  score    219871.0  wrote=yes  The White Lotus (2021) - S02E05 - That's Amore (1080p HMAX WEB-DL x265 Ghost).mkv
 1857  5cd02ada5f48482f94e7cfb3f26e4cda  offset     -0.13 s  score    129652.0  wrote=yes  BoJack Horseman (2014) - S00E01 - Sabrina's Christmas Wish (1080p BluRay x265 Ghost).mkv
 7098  2ddc2f6283434f73862fdda30b11b9e2  offset     -0.13 s  score    128033.0  wrote=yes  BoJack Horseman (2014) - S00E01 - Sabrina's Christmas Wish (1080p BluRay x265 Ghost).mkv
 1865  e2118caff4c3440f84cbd8bcb8296891  offset     -0.12 s  score    127350.0  wrote=yes  BoJack Horseman (2014) - S00E01 - Sabrina's Christmas Wish (1080p BluRay x265 Ghost).SYNCED.zho.srt
 8739  9e09e6310d904e0b9d09321857d79d01  offset     -0.12 s  score    254334.0  wrote=yes  Vargasommar S01E02.mkv
 3540  dfda1836e0b84d5d9d461542b5765969  offset     -0.11 s  score    100901.0  wrote=yes  The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
11178  0e9f0eacde0a48d298b4aa8adb38a9ce  offset     -0.11 s  score    105220.0  wrote=yes  The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
11235  372a74c4d7b7494181f7eed0f0af558c  offset     -0.11 s  score    100901.0  wrote=yes  The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
 5745  6ea2b727878c4f0f85d71b747c8294da  offset      -0.1 s  score    131389.0  wrote=yes  BoJack Horseman (2014) - S01E10 - One Trick Pony (1080p BluRay x265 Ghost).mkv
  131  25d4f359d7d241bb82295af07c176fb3  offset     -0.09 s  score    131458.0  wrote=yes  BoJack Horseman (2014) - S02E08 - Let's Find Out (1080p BluRay x265 Ghost).mkv
  256  520220cb3f9f47c4b2672b774bbec080  offset     -0.09 s  score    127781.0  wrote=yes  BoJack Horseman (2014) - S03E02 - The BoJack Horseman Show (1080p NF WEB-DL x265 Ghost).mkv
 5676  5871127747254cce8deecf507969f8a0  offset     -0.09 s  score    124200.0  wrote=yes  BoJack Horseman (2014) - S01E08 - The Telescope (1080p BluRay x265 Ghost).mkv
   89  6683c66a567741ebb92703141ca6b53a  offset     -0.08 s  score    130588.0  wrote=yes  BoJack Horseman (2014) - S02E06 - Higher Love (1080p BluRay x265 Ghost).mkv
  187  429dd8169d66491db1c26167669ee981  offset     -0.08 s  score    134505.0  wrote=yes  BoJack Horseman (2014) - S02E11 - Escape from L.A. (1080p BluRay x265 Ghost).mkv
 1050  f4100be00ecd4ac09667866588d02d54  offset     -0.08 s  score    100766.0  wrote=yes  Threesome (2021) - S01E04 - Bar Therapy WEBDL-1080p.mkv
 1087  bf30c49830cb4c22883a172dff235bb3  offset     -0.08 s  score    118926.0  wrote=yes  BoJack Horseman (2014) - S05E03 - Planned Obsolescence (1080p NF WEB-DL x265 Ghost).mkv
 5522  e32893f189b945caa2dea8cba5003618  offset     -0.08 s  score    121089.0  wrote=yes  BoJack Horseman (2014) - S01E04 - Zoës and Zeldas (1080p BluRay x265 Ghost).mkv
 7202  e633867f84374d33a8cbbb40eac438ec  offset     -0.08 s  score    190831.0  wrote=yes  Deliver Me (2024) - S01E01 - What Have You Done WEBDL-1080p.mkv
 9006  3388fd9e13f84c03972b333867cb73f4  offset     -0.08 s  score    289248.0  wrote=yes  The White Lotus (2021) - S03E04 - Hide or Seek (1080p HULU WEB-DL x265 Ghost).mkv
11130  105cd7b42b9f4d5f960343eaae2d9992  offset     -0.08 s  score    118332.0  wrote=yes  The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
  110  4e1938ebad2949f38766e25da69ed06e  offset     -0.07 s  score    123161.0  wrote=yes  BoJack Horseman (2014) - S02E07 - Hank After Dark (1080p BluRay x265 Ghost).mkv
 2497  1716a26c5f6e4411bba5776675ef8a0e  offset     -0.07 s  score    262390.0  wrote=no   The Lighthouse (2019) Bluray-1080p.mkv
11303  248c6c4668924078a8c7a5666b8a0f4a  offset     -0.07 s  score    105697.0  wrote=no   The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
  277  14b31b0dddd44cdf8c0efd7828a8cb1e  offset     -0.05 s  score    126365.0  wrote=yes  BoJack Horseman (2014) - S03E03 - BoJack Kills (1080p NF WEB-DL x265 Ghost).mkv
 6010  43c6e9222b6a429286999fcbb80018fb  offset     -0.05 s  score    127242.0  wrote=yes  BoJack Horseman (2014) - S02E08 - Let's Find Out (1080p BluRay x265 Ghost).mkv
  167  8f87b6d9caf54768989acbdd727e68b9  offset     -0.04 s  score    126041.0  wrote=yes  BoJack Horseman (2014) - S02E10 - Yes and... (1080p BluRay x265 Ghost).mkv
  908  e452c703b6e74919b61c05262683f092  offset     -0.04 s  score    107414.0  wrote=yes  Threesome (2021) - S01E01 - The Threesome WEBDL-1080p.mkv
 1111  30ba3cb2f6b643d880a30d3f15e14479  offset     -0.04 s  score    121298.0  wrote=yes  BoJack Horseman (2014) - S05E03 - Planned Obsolescence (1080p NF WEB-DL x265 Ghost).SYNCED.zho.srt
 1343  f4026a289a824670ac437eb385d4500e  offset     -0.04 s  score    133057.0  wrote=yes  BoJack Horseman (2014) - S05E07 - INT. SUB (1080p NF WEB-DL x265 Ghost).SYNCED.zho.srt
 2411  c1e2de2c9da34c569383b22fc2b8794a  offset     -0.04 s  score    293306.0  wrote=yes  The White Lotus (2021) - S03E04 - Hide or Seek (1080p HULU WEB-DL x265 Ghost).SYNCED.zho.srt
 5598  1db6d1fc563549719f615319ed8a5f0c  offset     -0.04 s  score    126088.0  wrote=yes  BoJack Horseman (2014) - S01E06 - Our A-Story is a “D”-Story (1080p BluRay x265 Ghost).mkv
 5798  67bc4e82f3d84567bd1785999106b01e  offset     -0.04 s  score    113711.0  wrote=yes  BoJack Horseman (2014) - S01E11 - Downer Ending (1080p BluRay x265 Ghost).mkv
 5977  8959e9b6c6644f2a9ee53d5777338689  offset     -0.04 s  score    126079.0  wrote=yes  BoJack Horseman (2014) - S02E06 - Higher Love (1080p BluRay x265 Ghost).mkv
 5994  cb1d5d5721bc455dbf4a92153204e7d5  offset     -0.04 s  score    119313.0  wrote=yes  BoJack Horseman (2014) - S02E07 - Hank After Dark (1080p BluRay x265 Ghost).mkv
 6062  e160fae26a944a26981787878a3b4e1a  offset     -0.04 s  score    129000.0  wrote=yes  BoJack Horseman (2014) - S02E11 - Escape from L.A. (1080p BluRay x265 Ghost).mkv
 6564  e3ad07e3a6394bfc96b492bb217d4ec7  offset     -0.04 s  score    119268.0  wrote=yes  BoJack Horseman (2014) - S05E03 - Planned Obsolescence (1080p NF WEB-DL x265 Ghost).mkv
 7279  ccd21eda4afe42b383106c7fe4c49c98  offset     -0.04 s  score    403562.0  wrote=yes  Jalla! Jalla!.mkv
 2023  ed9856b70e724a159fd6ba6b660ab164  offset     -0.03 s  score     96963.0  wrote=no   
11895  d9b06de8bdeb4b8c807d329fe87978d0  offset     -0.03 s  score     96963.0  wrote=no   The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv
  650  de23418ba80a48ae975aa9652b4aedbc  offset     -0.02 s  score    677267.0  wrote=yes  Jurassic Park (1993) Bluray-1080p.SYNCED.zho.srt
 2268  7fe98ec4aed34ffe84de9316c37dd9a4  offset     -0.02 s  score    248710.0  wrote=no   Kronjuvelerna.2011.Swedish.1080p.BluRay.DTS-HD.x264-NDF.mkv
 8926  7e80bd1cc44e49f0b2dc6f76c072d69a  offset     -0.02 s  score    421089.0  wrote=yes  The White Lotus (2021) - S02E07 - Arrivederci (1080p HMAX WEB-DL x265 Ghost).mkv
  393  a04258b4c1084419ab30c12fce9a9e18  offset     -0.01 s  score    134279.0  wrote=yes  BoJack Horseman (2014) - S03E08 - Old Acquaintance (1080p NF WEB-DL x265 Ghost).mkv
 2037  795f500fa0444339b33581151242c805  offset     -0.01 s  score    134241.0  wrote=no   The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv
 2050  9e9b13c817b54e93846655f042577eda  offset     -0.01 s  score    131213.0  wrote=no   The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv
 3549  d8bec190948a48c09f921f33b19bada8  offset     -0.01 s  score    106571.0  wrote=yes  The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
 6520  3232d380462643f483af51b33f112399  offset     -0.01 s  score    124265.0  wrote=no   The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv
 5198  d0d0ba29877b4c9388561a116851767f  offset     -0.01 s  score    674273.0  wrote=yes  Jurassic Park (1993) Bluray-1080p.mkv
11516  295132b71c7246bab1dc0cb0eaa78b6e  offset     -0.01 s  score    143520.0  wrote=yes  Arcane (2021) - S02E02 - Watch It All Burn (1080p BluRay x265 Ghost).mkv
12261  0873927ae4184fcc868b541d17e07669  offset     -0.01 s  score    131213.0  wrote=no   The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv
```

### 7.3 A file was written with a written offset larger than 5 s (31 jobs)

| job | file | written (ms) | align offset (s) | score | completion line |
|---|---|---|---|---|---|
| `ad8cea7b036744218b6c436ec9191e06` | BoJack Horseman (2014) - S01E02 - BoJack Hates the Troops (1080p BluRay x265 Ghost).mkv | 167533 | 148.51 | 25051.16 | 4723 |
| `c7336759e2864ef2a38c2504fa2341d5` | Farsan (2010).H.264.Prime-WEB-DL.mkv | 139106 | 139.05 | 731.646 | 4016 |
| `45d8d614ed124e59a07c6017bde287a2` | Farsan (2010).H.264.Prime-WEB-DL.mkv | 139106 | 139.05 | 731.646 | 12902 |
| `6265c565f9b84838811b86e86555cbc0` | Jurassic Park (1993) Bluray-1080p.mkv | -119928 | 2.34 | 215863.0 | 4037 |
| `29a392efe07f4569a8ea67f2dbf7e239` | Jurassic Park (1993) Bluray-1080p.mkv | -119928 | 2.34 | 215863.0 | 13325 |
| `e71951516d8c408db61a292baa34feea` | The Helicopter Heist - S01E08 - Too Close to the Sun WEBDL-1080p.mkv | 98693 | 56.9 | 63975.965 | 5673 |
| `66110847a9154b4cb89504c640dc83ad` | The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv | -86300 | -28.54 | 39751.0 | 3842 |
| `5a1e258abf5548edbab82b65b80b6112` | The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv | -86300 | -28.54 | 39751.0 | 13264 |
| `b031527d3f55499e9f61944d0cf40292` | The Helicopter Heist - S01E05 - Making Tsunami WEBDL-1080p.mkv | 60274 | 20.45 | 113111.08 | 4312 |
| `72744fabf04e4816a095b1d93cfb1344` | The Helicopter Heist - S01E07 - The Hunt WEBDL-1080p.mkv | -58373 | -5.24 | 80654.0 | 4394 |
| `396722f3e44549dcb779c7562ec08194` | The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv | -57540 | -57.54 | 62077.0 | 13198 |
| `ef0df6046cf84a24b29238800e58db83` | The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv | -57450 | -57.45 | 59996.0 | 13249 |
| `06ea3a36ab134bb48cef70da4ba8f832` | The Helicopter Heist - S01E04 - Cat and Mouse WEBDL-1080p.mkv | -56220 | 0.36 | 111941.0 | 4229 |
| `ebf91dc1bda04500bbe3e02a17b1e71d` | Vargasommar S01E04.mkv | -56035 | -101.66 | 149871.432 | 8855 |
| `ae66c480d6884aa68dd0a0906e3aea62` | American.Nightmare.2024.S01E03.1080p.WEB.h264-GP-TV-NLsubs.mkv | -53509 | 1.35 | 94354.0 | 3093 |
| `b077ba7e5fba4c37a72b2076aaf3c42d` | American.Nightmare.2024.S01E03.1080p.WEB.h264-GP-TV-NLsubs.mkv | -53509 | 1.35 | 94354.0 | 10542 |
| `8355eb46c6084dee845e900e833802c8` | The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv | -45744 | 0.21 | 129345.0 | 12204 |
| `08fed8a2e279470196ab726640ad6730` | Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv | -23196 | 11.97 | 116311.0 | 11000 |
| `2ad489446ca74426abb8706984658031` | Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv | -23017 | 12.04 | 116523.0 | 10979 |
| `ed2aec15ed14488eb3ddaa854a1f4837` | Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv | -23017 | 12.04 | 116523.0 | 11028 |
| `1170914f61bd4c0b8b1ca549d694db54` | The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv | -22988 | 21.93 | 132257.0 | 12223 |
| `1037b11ac97d468d99e7aa484983b370` | Gosta.s01e09.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | 21831 | 50.03 | 47940.0 | 10938 |
| `63c9d45facf344499675c4e92fa4e23b` | The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv | -15769 | 42.03 | 95522.0 | 3605 |
| `871ff2acb6f2431e85375f04110c98c7` | The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv | -15769 | 42.03 | 95522.0 | 11867 |
| `ff1c2f6564164698836fe65d09cebfc7` | The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv | -14686 | 42.67 | 85322.0 | 3625 |
| `cb0dd08064b249f8b741baa6fc7005a0` | The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv | -14337 | 42.33 | 99634.0 | 11834 |
| `f370e7494ed545678f62e80bff42ac2c` | The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv | -13471 | 20.03 | 69326.0 | 3517 |
| `1bc9cec0d1624148aeef43a1428f05c2` | The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv | -13471 | 20.03 | 69326.0 | 12740 |
| `3e615acc7d1c4f37aa1277d9636f2447` | Gosta.s01e02.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | 13078 | 44.92 | 31640.0 | 10820 |
| `a4b96e714c324f4cbca7d45997bf0319` | Wake - S01E03 - Thicker Than Water WEBDL-1080p.mkv | 7735 | 57.18 | 112255.0 | 4204 |
| `f094db5567ec4681b3494f057698cd44` | Gosta.s01e10.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | -5571 | 27.72 | 45627.0 | 10987 |

### 7.4 The written offset is negative (86 jobs) — the subtitle was moved backwards

```
 4037  6265c565f9b84838811b86e86555cbc0  written  -119928 ms  align      2.34 s  score    215863.0  Jurassic Park (1993) Bluray-1080p.mkv
13325  29a392efe07f4569a8ea67f2dbf7e239  written  -119928 ms  align      2.34 s  score    215863.0  Jurassic Park (1993) Bluray-1080p.mkv
 3842  66110847a9154b4cb89504c640dc83ad  written   -86300 ms  align    -28.54 s  score     39751.0  The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv
13264  5a1e258abf5548edbab82b65b80b6112  written   -86300 ms  align    -28.54 s  score     39751.0  The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv
 4394  72744fabf04e4816a095b1d93cfb1344  written   -58373 ms  align     -5.24 s  score     80654.0  The Helicopter Heist - S01E07 - The Hunt WEBDL-1080p.mkv
13198  396722f3e44549dcb779c7562ec08194  written   -57540 ms  align    -57.54 s  score     62077.0  The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv
13249  ef0df6046cf84a24b29238800e58db83  written   -57450 ms  align    -57.45 s  score     59996.0  The Helicopter Heist - S01E01 - Best Friends WEBDL-1080p.mkv
 4229  06ea3a36ab134bb48cef70da4ba8f832  written   -56220 ms  align      0.36 s  score    111941.0  The Helicopter Heist - S01E04 - Cat and Mouse WEBDL-1080p.mkv
 8855  ebf91dc1bda04500bbe3e02a17b1e71d  written   -56035 ms  align   -101.66 s  score  149871.432  Vargasommar S01E04.mkv
 3093  ae66c480d6884aa68dd0a0906e3aea62  written   -53509 ms  align      1.35 s  score     94354.0  American.Nightmare.2024.S01E03.1080p.WEB.h264-GP-TV-NLsubs.mkv
10542  b077ba7e5fba4c37a72b2076aaf3c42d  written   -53509 ms  align      1.35 s  score     94354.0  American.Nightmare.2024.S01E03.1080p.WEB.h264-GP-TV-NLsubs.mkv
12204  8355eb46c6084dee845e900e833802c8  written   -45744 ms  align      0.21 s  score    129345.0  The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv
11000  08fed8a2e279470196ab726640ad6730  written   -23196 ms  align     11.97 s  score    116311.0  Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv
10979  2ad489446ca74426abb8706984658031  written   -23017 ms  align     12.04 s  score    116523.0  Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv
11028  ed2aec15ed14488eb3ddaa854a1f4837  written   -23017 ms  align     12.04 s  score    116523.0  Arcane (2021) - S01E03 - The Base Violence Necessary for Change (1080p BluRay x265 Ghost).mkv
12223  1170914f61bd4c0b8b1ca549d694db54  written   -22988 ms  align     21.93 s  score    132257.0  The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv
 3605  63c9d45facf344499675c4e92fa4e23b  written   -15769 ms  align     42.03 s  score     95522.0  The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv
11867  871ff2acb6f2431e85375f04110c98c7  written   -15769 ms  align     42.03 s  score     95522.0  The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv
 3625  ff1c2f6564164698836fe65d09cebfc7  written   -14686 ms  align     42.67 s  score     85322.0  The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv
11834  cb0dd08064b249f8b741baa6fc7005a0  written   -14337 ms  align     42.33 s  score     99634.0  The Unlikely Murderer - S01E03 - Episode 3 WEBDL-1080p.mkv
 3517  f370e7494ed545678f62e80bff42ac2c  written   -13471 ms  align     20.03 s  score     69326.0  The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv
12740  1bc9cec0d1624148aeef43a1428f05c2  written   -13471 ms  align     20.03 s  score     69326.0  The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv
10987  f094db5567ec4681b3494f057698cd44  written    -5571 ms  align     27.72 s  score     45627.0  Gosta.s01e10.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv
11025  f35acd4f02df42bba6fdbdee665ae4ad  written    -1793 ms  align     33.29 s  score     45751.0  Gosta.s01e11.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv
 4162  41dd60b84e7745e6b74f52633c543ad7  written    -1200 ms  align     -1.29 s  score   135825.56  The Helicopter Heist - S01E03 - The Devil Is in the Details WEBDL-1080p.mkv
 8731  e913e30d6feb4cd4b9de9c017c527618  written    -1025 ms  align      0.19 s  score    243267.0  Vargasommar S01E01.mkv
 2246  e8572413da9c48dc97c21d3d59e9f638  written     -340 ms  align     -0.34 s  score    224761.0  The White Lotus (2021) - S02E01 - Ciao (1080p HMAX WEB-DL x265 Ghost).mkv
 2330  ea3ea265c9ff43c791a4e802051cf961  written     -330 ms  align     -0.33 s  score    222936.0  The White Lotus (2021) - S02E06 - Abductions (1080p HMAX WEB-DL x265 Ghost).mkv
  300  88d872c92271458ca018e851956f8499  written     -290 ms  align     -0.29 s  score    130091.0  BoJack Horseman (2014) - S03E04 - Fish Out Of Water (1080p NF WEB-DL x265 Ghost).mkv
 2284  4eb295c5d42647be94a3c093e00ecc54  written     -240 ms  align     -0.24 s  score    247333.0  The White Lotus (2021) - S02E03 - Bull Elephants (1080p HMAX WEB-DL x265 Ghost).mkv
 8813  2aae2b21091740219fce641694d4ff92  written     -230 ms  align     -0.23 s  score    222245.0  The White Lotus (2021) - S02E01 - Ciao (1080p HMAX WEB-DL x265 Ghost).mkv
 8830  43b60648eaa04c70a90733bc5d506773  written     -180 ms  align     -0.18 s  score    227675.0  The White Lotus (2021) - S02E02 - Italian Dream (1080p HMAX WEB-DL x265 Ghost).mkv
 8847  3c690b59cb0d41368e6566ec2e5a5f0b  written     -180 ms  align     -0.18 s  score    240487.0  The White Lotus (2021) - S02E03 - Bull Elephants (1080p HMAX WEB-DL x265 Ghost).mkv
 8872  0cce38be02114e139eac4fa4ac734a1b  written     -180 ms  align     -0.18 s  score    188615.0  The White Lotus (2021) - S02E04 - In the Sandbox (1080p HMAX WEB-DL x265 Ghost).mkv
 8883  9c90d7ef422c42099db4b77b3a6c49da  written     -170 ms  align     -0.17 s  score    219871.0  The White Lotus (2021) - S02E05 - That's Amore (1080p HMAX WEB-DL x265 Ghost).mkv
 3675  ba63b38277df4581ab76b609af2fe945  written     -150 ms  align     -0.53 s  score    154636.4  Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
 1858  5cd02ada5f48482f94e7cfb3f26e4cda  written     -130 ms  align     -0.13 s  score    129652.0  BoJack Horseman (2014) - S00E01 - Sabrina's Christmas Wish (1080p BluRay x265 Ghost).mkv
 7099  2ddc2f6283434f73862fdda30b11b9e2  written     -130 ms  align     -0.13 s  score    128033.0  BoJack Horseman (2014) - S00E01 - Sabrina's Christmas Wish (1080p BluRay x265 Ghost).mkv
 1866  e2118caff4c3440f84cbd8bcb8296891  written     -120 ms  align     -0.12 s  score    127350.0  BoJack Horseman (2014) - S00E01 - Sabrina's Christmas Wish (1080p BluRay x265 Ghost).SYNCED.zho.srt
 8740  9e09e6310d904e0b9d09321857d79d01  written     -120 ms  align     -0.12 s  score    254334.0  Vargasommar S01E02.mkv
 3543  dfda1836e0b84d5d9d461542b5765969  written     -110 ms  align     -0.11 s  score    100901.0  The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
11180  0e9f0eacde0a48d298b4aa8adb38a9ce  written     -110 ms  align     -0.11 s  score    105220.0  The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
11237  372a74c4d7b7494181f7eed0f0af558c  written     -110 ms  align     -0.11 s  score    100901.0  The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
 5746  6ea2b727878c4f0f85d71b747c8294da  written     -100 ms  align      -0.1 s  score    131389.0  BoJack Horseman (2014) - S01E10 - One Trick Pony (1080p BluRay x265 Ghost).mkv
  132  25d4f359d7d241bb82295af07c176fb3  written      -90 ms  align     -0.09 s  score    131458.0  BoJack Horseman (2014) - S02E08 - Let's Find Out (1080p BluRay x265 Ghost).mkv
  257  520220cb3f9f47c4b2672b774bbec080  written      -90 ms  align     -0.09 s  score    127781.0  BoJack Horseman (2014) - S03E02 - The BoJack Horseman Show (1080p NF WEB-DL x265 Ghost).mkv
 5677  5871127747254cce8deecf507969f8a0  written      -90 ms  align     -0.09 s  score    124200.0  BoJack Horseman (2014) - S01E08 - The Telescope (1080p BluRay x265 Ghost).mkv
   90  6683c66a567741ebb92703141ca6b53a  written      -80 ms  align     -0.08 s  score    130588.0  BoJack Horseman (2014) - S02E06 - Higher Love (1080p BluRay x265 Ghost).mkv
  188  429dd8169d66491db1c26167669ee981  written      -80 ms  align     -0.08 s  score    134505.0  BoJack Horseman (2014) - S02E11 - Escape from L.A. (1080p BluRay x265 Ghost).mkv
 1084  f4100be00ecd4ac09667866588d02d54  written      -80 ms  align     -0.08 s  score    100766.0  Threesome (2021) - S01E04 - Bar Therapy WEBDL-1080p.mkv
 1088  bf30c49830cb4c22883a172dff235bb3  written      -80 ms  align     -0.08 s  score    118926.0  BoJack Horseman (2014) - S05E03 - Planned Obsolescence (1080p NF WEB-DL x265 Ghost).mkv
 5523  e32893f189b945caa2dea8cba5003618  written      -80 ms  align     -0.08 s  score    121089.0  BoJack Horseman (2014) - S01E04 - Zoës and Zeldas (1080p BluRay x265 Ghost).mkv
 7203  e633867f84374d33a8cbbb40eac438ec  written      -80 ms  align     -0.08 s  score    190831.0  Deliver Me (2024) - S01E01 - What Have You Done WEBDL-1080p.mkv
 9007  3388fd9e13f84c03972b333867cb73f4  written      -80 ms  align     -0.08 s  score    289248.0  The White Lotus (2021) - S03E04 - Hide or Seek (1080p HULU WEB-DL x265 Ghost).mkv
11132  105cd7b42b9f4d5f960343eaae2d9992  written      -80 ms  align     -0.08 s  score    118332.0  The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
  111  4e1938ebad2949f38766e25da69ed06e  written      -70 ms  align     -0.07 s  score    123161.0  BoJack Horseman (2014) - S02E07 - Hank After Dark (1080p BluRay x265 Ghost).mkv
 7180  3e9e9f5a844e44aba7e929b4127cb166  written      -70 ms  align     -1.74 s  score   159816.44  The Helicopter Heist - S01E07 - The Hunt WEBDL-1080p.mkv
 2320  b583edcf099640288635f2f89be7d23b  written      -60 ms  align      0.49 s  score   202421.84  The White Lotus (2021) - S02E05 - That's Amore (1080p HMAX WEB-DL x265 Ghost).mkv
  278  14b31b0dddd44cdf8c0efd7828a8cb1e  written      -50 ms  align     -0.05 s  score    126365.0  BoJack Horseman (2014) - S03E03 - BoJack Kills (1080p NF WEB-DL x265 Ghost).mkv
 6011  43c6e9222b6a429286999fcbb80018fb  written      -50 ms  align     -0.05 s  score    127242.0  BoJack Horseman (2014) - S02E08 - Let's Find Out (1080p BluRay x265 Ghost).mkv
  168  8f87b6d9caf54768989acbdd727e68b9  written      -40 ms  align     -0.04 s  score    126041.0  BoJack Horseman (2014) - S02E10 - Yes and... (1080p BluRay x265 Ghost).mkv
  919  e452c703b6e74919b61c05262683f092  written      -40 ms  align     -0.04 s  score    107414.0  Threesome (2021) - S01E01 - The Threesome WEBDL-1080p.mkv
 1112  30ba3cb2f6b643d880a30d3f15e14479  written      -40 ms  align     -0.04 s  score    121298.0  BoJack Horseman (2014) - S05E03 - Planned Obsolescence (1080p NF WEB-DL x265 Ghost).SYNCED.zho.srt
 1344  f4026a289a824670ac437eb385d4500e  written      -40 ms  align     -0.04 s  score    133057.0  BoJack Horseman (2014) - S05E07 - INT. SUB (1080p NF WEB-DL x265 Ghost).SYNCED.zho.srt
 2274  85f136dedd0140e5a0b863020f554709  written      -40 ms  align     -0.18 s  score   196837.88  The Helicopter Heist - S01E07 - The Hunt WEBDL-1080p.mkv
 2412  c1e2de2c9da34c569383b22fc2b8794a  written      -40 ms  align     -0.04 s  score    293306.0  The White Lotus (2021) - S03E04 - Hide or Seek (1080p HULU WEB-DL x265 Ghost).SYNCED.zho.srt
 5599  1db6d1fc563549719f615319ed8a5f0c  written      -40 ms  align     -0.04 s  score    126088.0  BoJack Horseman (2014) - S01E06 - Our A-Story is a “D”-Story (1080p BluRay x265 Ghost).mkv
 5799  67bc4e82f3d84567bd1785999106b01e  written      -40 ms  align     -0.04 s  score    113711.0  BoJack Horseman (2014) - S01E11 - Downer Ending (1080p BluRay x265 Ghost).mkv
 5978  8959e9b6c6644f2a9ee53d5777338689  written      -40 ms  align     -0.04 s  score    126079.0  BoJack Horseman (2014) - S02E06 - Higher Love (1080p BluRay x265 Ghost).mkv
 5995  cb1d5d5721bc455dbf4a92153204e7d5  written      -40 ms  align     -0.04 s  score    119313.0  BoJack Horseman (2014) - S02E07 - Hank After Dark (1080p BluRay x265 Ghost).mkv
 6063  e160fae26a944a26981787878a3b4e1a  written      -40 ms  align     -0.04 s  score    129000.0  BoJack Horseman (2014) - S02E11 - Escape from L.A. (1080p BluRay x265 Ghost).mkv
 6565  e3ad07e3a6394bfc96b492bb217d4ec7  written      -40 ms  align     -0.04 s  score    119268.0  BoJack Horseman (2014) - S05E03 - Planned Obsolescence (1080p NF WEB-DL x265 Ghost).mkv
 7281  ccd21eda4afe42b383106c7fe4c49c98  written      -40 ms  align     -0.04 s  score    403562.0  Jalla! Jalla!.mkv
 9058  ffb11b7199274641960943e07ba4d310  written      -40 ms  align     -5.15 s  score   139492.36  The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv
  651  de23418ba80a48ae975aa9652b4aedbc  written      -20 ms  align     -0.02 s  score    677267.0  Jurassic Park (1993) Bluray-1080p.SYNCED.zho.srt
 8927  7e80bd1cc44e49f0b2dc6f76c072d69a  written      -20 ms  align     -0.02 s  score    421089.0  The White Lotus (2021) - S02E07 - Arrivederci (1080p HMAX WEB-DL x265 Ghost).mkv
12427  0ad9f1f11fa74ae4b9c5648f9f1f7689  written      -20 ms  align     -0.58 s  score   148068.08  Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
  394  a04258b4c1084419ab30c12fce9a9e18  written      -10 ms  align     -0.01 s  score    134279.0  BoJack Horseman (2014) - S03E08 - Old Acquaintance (1080p NF WEB-DL x265 Ghost).mkv
 2467  aa6a8a893e754d90b9b306e443e34508  written      -10 ms  align     -5.12 s  score   139684.36  The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv
 3589  debd6b7dc1604e5baa0f1016910bf870  written      -10 ms  align     -0.54 s  score   148768.88  Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
 3551  d8bec190948a48c09f921f33b19bada8  written      -10 ms  align     -0.01 s  score    106571.0  The Unlikely Murderer - S01E01 - Episode 1 WEBDL-1080p.mkv
 5203  d0d0ba29877b4c9388561a116851767f  written      -10 ms  align     -0.01 s  score    674273.0  Jurassic Park (1993) Bluray-1080p.mkv
 9582  7c8a35f82ddd4392a2683288408d6896  written      -10 ms  align     -0.53 s  score   153851.12  Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
11517  295132b71c7246bab1dc0cb0eaa78b6e  written      -10 ms  align     -0.01 s  score    143520.0  Arcane (2021) - S02E02 - Watch It All Burn (1080p BluRay x265 Ghost).mkv
12107  77db6d829fd846838f32aaa78ae3fc83  written      -10 ms  align     -0.54 s  score   148768.88  Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv
11774  267edd18414c4a098b2fff7c2c0d36ec  written      -10 ms  align      0.94 s  score  104814.551  The Puppet Master - Hunting the Ultimate Conman - S01E02 - Chasing a Ghost WEBDL-1080p.mkv
```

Distribution of the 156 jobs that reported an offset in `change=` (absolute value): min 10 ms, Q1 40.0, median 80.0, Q3 292.5, max 167533 ms. The typical write is therefore a 10–300 ms correction, and the 31 writes above 5 000 ms sit 62×–2094× the median; nothing in the log claims those are wrong (an alternate-language subtitle against a different cut legitimately needs a big shift), which is why they are listed for inspection rather than called defects.

### 7.5 Engine scores that are statistical outliers (71 of 1002 jobs)

Family definition: the scored alignments themselves, Tukey 1,5×IQR — Q1 132242.0, median 162570.0, Q3 233608.25, IQR 101366.25, so anything below **-19807.375** or above **385657.625** is an outlier. Min score -70725.0 (job `7f0b9ce2…`), max 677281.0 (job `2fd99c6d…`).

| job | file | score | offset (s) | verdict | line |
|---|---|---|---|---|---|
| `7f0b9ce247214e7d9b5612eeca9ca2fb` | Spermageddon (2024) Bluray-1080p.mp4 | -70725.0 | 0.0 | completed | 3511 |
| `7e13311e4e214c9a95c1aedcced2e987` | Spermageddon (2024) Bluray-1080p.mp4 | -70725.0 | 0.0 | completed | 12097 |
| `155dd60e4ded4ed287bd7da861cb4081` | Solsidan.S09E03.mkv | -60411.0 | -143.8 | completed | 8704 |
| `6f32e35558f3411b961ee34d931e0899` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -41855.162 | -144.38 | completed | 3631 |
| `3a8fbe3d24194ae9bf444ff3064beb65` | Solsidan.S09E01.mkv | -41642.0 | -149.99 | completed | 8695 |
| `ba3b459ab20b47448296c0b4d8c0525f` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -37612.56 | -123.74 | completed | 11929 |
| `14c87ee5a5ec46c29ed6f87db09cea4b` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -36522.0 | -123.85 | completed | 3617 |
| `a38c80abd34345d0aeeb73b58132d97c` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -36522.0 | -123.85 | completed | 11958 |
| `c5fe02743cf14a689c8503de0511bff8` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -29597.32 | -137.84 | completed | 11973 |
| `b85bd28eeacc4aee9ee680b0d672ae5a` | Black.Mirror.2011.S04E01.USS.Callister.1080p.NF.WEB-DL.Hybrid.H265.DV.HDR.DDP.5.1.English.DarQ.mkv | 386090.0 | 0.0 | completed | 11335 |
| `99000993e57d47119622158a087894be` | The White Lotus (2021) - S03E08 - Amor Fati (1080p HULU WEB-DL x265 Ghost).mkv | 388553.0 | 0.0 | completed | 2484 |
| `52447ad90bf440819dae0e5cfef29e27` | The Ugly Stepsister (2025) Bluray-1080p.mkv | 394442.0 | 0.08 | UNVERIFIED | 2098 |
| `ffa73e326e3e442e8d5de6cb322400a4` | Black.Mirror.2011.S04E06.Black.Museum.1080p.NF.WEB-DL.Hybrid.H265.DV.HDR.DDP.5.1.English.DarQ.mkv | 401753.0 | 0.0 | completed | 11794 |
| `39f62a93279f42bbacc573a84e68995a` | Tillsammans.2000.1080p.x265.Opus.PERFEKT.mkv | 402055.0 | 0.0 | completed | 1953 |
| `ccd21eda4afe42b383106c7fe4c49c98` | Jalla! Jalla!.mkv | 403562.0 | -0.04 | completed | 7279 |
| `28465e94fbbb484cb84d9fa0c444c89e` | Black.Mirror.S07E03.Hotel.Reverie.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-FLUX.mkv | 403770.0 | 0.0 | completed | 5206 |
| `3fe0d383edcf4535a5aff0e1da24b6c2` | The White Lotus (2021) - S03E08 - Amor Fati (1080p HULU WEB-DL x265 Ghost).mkv | 405653.0 | 0.0 | completed | 9555 |
| `c9381fc59381468eaac9bb91b8f7e78a` | Black.Mirror.S07E03.Hotel.Reverie.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-FLUX.mkv | 405694.0 | 0.0 | completed | 5220 |
| `efa123d4e9f94f61ab6a81b689e2624c` |  | 405889.0 | 0.0 | completed | 2492 |
| `e92563de3f6945e68450b7438e1caf48` | The White Lotus (2021) - S03E08 - Amor Fati (1080p HULU WEB-DL x265 Ghost).mkv | 405933.0 | 0.0 | completed | 9068 |
| `085e5c371a824c5990136ecc9bbd64d5` | Tusen Gånger Starkare.mkv | 407402.0 | 0.0 | completed | 8035 |
| `ca8e3e36830b47b4bda457119205f5b8` | Tusen Gånger Starkare.mkv | 407402.0 | 0.0 | completed | 12637 |
| `7e80bd1cc44e49f0b2dc6f76c072d69a` | The White Lotus (2021) - S02E07 - Arrivederci (1080p HMAX WEB-DL x265 Ghost).mkv | 421089.0 | -0.02 | completed | 8926 |
| `8221c83b0e6c4c17b5cf367b94273700` | Black.Mirror.S02E04.2013.1080p.Netflix.WEB-DL.AVC.AAC.2.0-DBTV.mkv | 423838.0 | 0.0 | completed | 10783 |
| `cd4a924e7db64f40bd5043d20bd199c0` | Black Mirror (2011) S03E06 Hated in the Nation (1080p NF WEB-DL Hybrid H265 DV HDR DDP 5.1 English - DarQ).mkv | 431542.0 | 0.0 | completed | 11211 |
| `fff806b2003b430cb672c7b4efd43380` | Black.Mirror.2011.S06E03.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv | 432282.0 | 0.0 | completed | 12291 |
| `9977abff8a894883a79cd4243dcc0122` | Black.Mirror.2011.S06E03.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv | 434354.0 | 0.0 | completed | 12185 |
| `84f3a1c6667c4fb3abe26b833d90cd91` | Black.Mirror.2011.S06E03.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv | 434802.0 | 0.0 | completed | 4923 |
| `a3fd2c06be7a4b6cb74942ef3a97060e` | Black.Mirror.2011.S06E03.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv | 434802.0 | 0.0 | completed | 12232 |
| `f7637eaf8a864ab79ee33a3e4839cc2c` | Black.Mirror.2011.S06E03.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv | 434994.0 | 0.0 | completed | 12155 |
| `089658d5ec6e43898d6f9a08a5dea829` | Black.Mirror.2011.S06E03.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv | 435802.0 | 0.0 | completed | 4935 |
| `f0f214fce5f74b69921a96324244b7ac` | Black.Mirror.2011.S04E01.USS.Callister.1080p.NF.WEB-DL.Hybrid.H265.DV.HDR.DDP.5.1.English.DarQ.mkv | 436930.0 | 0.0 | completed | 11319 |
| `a2ee03f21b1a4bc09c91119deafac57c` | Tillsammans.2000.1080p.x265.Opus.PERFEKT.mkv | 465431.0 | 0.0 | completed | 7172 |
| `2dded1e35f814e0f865abbdb962d21ad` | Tell.Me.Who.I.Am.2019.1080p.NF.WEB-DL.DDP5.1.X264-IKA-Obfuscated.mkv | 470311.0 | 0.08 | completed | 12301 |
| `ac3528c0939a46bb8dab62414e9489c8` | Black.Mirror.S07E06.USS.Callister.Into.Infinity.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-FLUX.mkv | 477449.0 | 0.0 | completed | 5372 |
| `30cfe7ee85b744b583649da766e7c2e8` | Black.Mirror.S07E06.USS.Callister.Into.Infinity.1080p.NF.WEB-DL.DDP5.1.Atmos.H.264-FLUX.mkv | 481105.0 | 0.0 | completed | 5408 |
| `0696b4ddf6544c31911446f8c11b6dd0` | Black Mirror (2011) S03E06 Hated in the Nation (1080p NF WEB-DL Hybrid H265 DV HDR DDP 5.1 English - DarQ).mkv | 486766.0 | 0.0 | completed | 11217 |
| `376552737a8b45fb91bb5eb841a8fdba` | Egghead.Republic.2025.1080p.WEB.H264-AFO.mkv | 491166.0 | 0.0 | completed | 4495 |
| `2efac5cba5fd475b899f53ffdc6ec01c` | Tell.Me.Who.I.Am.2019.1080p.NF.WEB-DL.DDP5.1.X264-IKA-Obfuscated.mkv | 493635.0 | 0.0 | completed | 7597 |
| `4d815ed273cb405f8c28705122e68c89` | The Dance Club 2025 H.264 EAC3.mkv | 506166.0 | 0.0 | completed | 4445 |
| `0474c4bb03df400cba0c5309444c4b7f` | The Dance Club 2025 H.264 EAC3.mkv | 506166.0 | 0.0 | completed | 10711 |
| `47e524b5a8c44a4e8289cbe7aa9c7578` |  | 507129.0 | 0.0 | completed | 905 |
| `cafd6be4efee4f7490d2bfeacc8c44fd` | Tell.Me.Who.I.Am.2019.1080p.NF.WEB-DL.DDP5.1.X264-IKA-Obfuscated.mkv | 507129.0 | 0.0 | completed | 12331 |
| `d4580c422a3b46b99cc643dc2ba51be0` | Coherence.2013.1080p.BluRay.x265.10bit.DTS-WiKi.mkv | 516245.0 | 0.0 | completed | 626 |
| `db8b3ff235d84c98b11e0bc3a9653a3d` | Coherence.2013.1080p.BluRay.x265.10bit.DTS-WiKi.mkv | 516245.0 | 0.0 | completed | 10664 |
| `86d83c3e829d4680aa21eeb4139dbed9` | Jurassic Park (1993) Bluray-1080p.mkv | 525637.0 | 0.0 | completed | 9221 |
| `e3b269b523fa4859a583dd9ec670fbef` | Carousel (2023) WEBDL-1080p.mkv | 536402.0 | 0.0 | completed | 7290 |
| `d85cc9537062400f866f5528f69cec4f` | tomten.ar.far.till.alla.barnen.1999.swedish.1080p.web.h264-norush.mkv | 537123.0 | 0.0 | completed | 7991 |
| `4d5acdb87d8e45109eeeb9a3dd41c8c7` | tomten.ar.far.till.alla.barnen.1999.swedish.1080p.web.h264-norush.mkv | 537123.0 | 0.0 | completed | 12599 |
| `937496a87f1b4f4e869841b41534af0e` | Sune på bilsemester.mkv | 544626.0 | 0.0 | completed | 7592 |
| `79ada260edc6496083459be2b89152ee` | Sune på bilsemester.mkv | 544626.0 | 0.0 | completed | 13071 |
| `e64366c1755e414c99030a43a6c22359` | Sune i Grekland.mkv | 545114.0 | 0.0 | completed | 7540 |
| `0c65e762f63a4208a597aed49bd5ba60` | Sune i Grekland.mkv | 545114.0 | 0.0 | completed | 13058 |
| `9bbd5f263c214a71bcf69caedea03425` | Pure (2010) WEBDL-1080p.mkv | 555794.0 | 0.0 | completed | 3361 |
| `4208af64c4724702ac78f1277a72ce2c` | Pure (2010) WEBDL-1080p.mkv | 555794.0 | 0.0 | completed | 11616 |
| `2fd99c6d1157485b892990c1303d1359` | Paradise.Is.Burning.2023.1080p.AMZN.WEB-DL.DDP5.1.H.264-MADSKY.mkv | 557949.0 | 0.97 | completed | 5139 |
| `5d99cc84a5744e3ba7acee58a420ee7e` |  | 590705.0 | 0.0 | completed | 869 |
| `8ab891a246d345f7acb94ebc5104ea73` | Take.Care.of.Maya.2023.1080p.NF.WEB-DL.DDP5.1.x264-CMRG.mkv | 591873.0 | 0.0 | completed | 7613 |
| `9e7133a6ea1a402d939e8a86f8288478` | Take.Care.of.Maya.2023.1080p.NF.WEB-DL.DDP5.1.x264-CMRG.mkv | 591873.0 | 0.0 | completed | 12270 |
| `3a5b6e68e0cb4c43a9795bad4c4f9063` | Take.Care.of.Maya.2023.1080p.NF.WEB-DL.DDP5.1.x264-CMRG.mkv | 593995.0 | 0.0 | completed | 7616 |
| `f3af07004b4f4a5686bc8d546c6dae3e` | Take.Care.of.Maya.2023.1080p.NF.WEB-DL.DDP5.1.x264-CMRG.mkv | 600271.0 | 0.0 | completed | 833 |
| `52c1cd3b91264bfca2fe1350eab6fbc3` | Take.Care.of.Maya.2023.1080p.NF.WEB-DL.DDP5.1.x264-CMRG.mkv | 600271.0 | 0.0 | completed | 12240 |
| `ba8f9d1ecb534bc6b64ecd6f5b85e1a7` | Take.Care.of.Maya.2023.1080p.NF.WEB-DL.DDP5.1.x264-CMRG.mkv | 600271.0 | 0.0 | completed | 12282 |
| `0416d0c3ebfb40efaab3421cc0cfa34a` | Ata.sova.do.2012.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | 606036.0 | 0.0 | completed | 3302 |
| `09d0947d7d014935aa472c993597357a` | Ata.sova.do.2012.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | 606036.0 | 0.0 | completed | 10693 |
| `5c716f86d7f54ee5a0e71c6b94a687f2` | Ett sista race (2023).mkv | 640475.0 | 0.0 | completed | 4498 |
| `1a5c39945e1e420ebaa1954c9c42ccb8` | Ett sista race (2023).mkv | 640475.0 | 0.0 | completed | 10770 |
| `f8b516f66746466586ca9ed976306189` | City of God (2002) Bluray-1080p.mkv | 666390.0 | 0.0 | completed | 12377 |
| `d0d0ba29877b4c9388561a116851767f` | Jurassic Park (1993) Bluray-1080p.mkv | 674273.0 | -0.01 | completed | 5198 |
| `de23418ba80a48ae975aa9652b4aedbc` | Jurassic Park (1993) Bluray-1080p.SYNCED.zho.srt | 677267.0 | -0.02 | completed | 650 |
| `50aacea85a73457ca81247b090c3aead` | Jurassic Park (1993) Bluray-1080p.mkv | 677281.0 | 0.0 | completed | 12436 |

Read plainly: the big negative scores are all `The Unlikely Murderer` (a 1-cue / 13-cue reference track being used as the ruler — the log says so in its own words) and `Solsidan S09`, and the big positive ones are , Ata.sova.do.2012.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv, Black Mirror (2011) S03E06 Hated in the Nation (1080p NF WEB-DL Hybrid H265 DV HDR DDP 5.1 English, Black.Mirror.2011.S04E01.USS.Callister.1080p.NF.WEB-DL.Hybrid.H265.DV.HDR.DDP.5.1.English.DarQ.mkv, Black.Mirror.2011.S04E06.Black.Museum.1080p.NF.WEB-DL.Hybrid.H265.DV.HDR.DDP.5.1.English.DarQ.mkv, Black.Mirror.2011.S06E03.1080p.NF.WEB-DL.H265.SDR.DDP.Atmos.5.1.English-HONE.mkv — no single file dominates the high side.

### 7.6 Negative engine scores — "the engine itself calls a negative score an unsuccessful sync" (13 jobs)

| job | file | score | offset (s) | verdict | wrote a file? |
|---|---|---|---|---|---|
| `7f0b9ce247214e7d9b5612eeca9ca2fb` | Spermageddon (2024) Bluray-1080p.mp4 | -70725.0 | 0.0 | completed | no — last alignment was the negative one |
| `7e13311e4e214c9a95c1aedcced2e987` | Spermageddon (2024) Bluray-1080p.mp4 | -70725.0 | 0.0 | completed | no — last alignment was the negative one |
| `155dd60e4ded4ed287bd7da861cb4081` | Solsidan.S09E03.mkv | -60411.0 | -143.8 | completed | yes |
| `6f32e35558f3411b961ee34d931e0899` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -41855.162 | -144.38 | completed | yes |
| `3a8fbe3d24194ae9bf444ff3064beb65` | Solsidan.S09E01.mkv | -41642.0 | -149.99 | completed | yes |
| `ba3b459ab20b47448296c0b4d8c0525f` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -37612.56 | -123.74 | completed | yes |
| `14c87ee5a5ec46c29ed6f87db09cea4b` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -36522.0 | -123.85 | completed | yes |
| `a38c80abd34345d0aeeb73b58132d97c` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -36522.0 | -123.85 | completed | yes |
| `c5fe02743cf14a689c8503de0511bff8` | The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.mkv | -29597.32 | -137.84 | completed | yes |
| `c35bf99dbbb54cc8be0b2a79b0c7e36d` | The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv | -14333.68 | -149.99 | completed | yes |
| `89dec4ae6a514918b03a8755e582fdaf` | The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv | -3481.721 | -121.88 | completed | yes |
| `d4d768364d14408a8e8f5650fa7dba26` | The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv | -3481.721 | -121.88 | completed | yes |
| `0e1392c9c28d41a2b9eec230fd371526` | The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.mkv | -2685.04 | -139.18 | completed | yes |

The distinction that matters: for 0 of these the negative score was a *discarded attempt*, and the plugin then re-aligned against the audio/second ruler with a positive score (that is the designed recovery: `job …: the reference subtitle s:N is not the same cut … — discarding that track as a ruler and aligning …`). For the other **13** the negative score is the **last** word, and 11 of those still wrote a file — table 7.7.

### 7.7 **A job whose only alignment failed still wrote a synced subtitle** — 13 jobs

These are the jobs whose last `ffsubsync alignment:` line carries a **negative** score, i.e. by the plugin's own log text "the engine itself calls a negative score an unsuccessful sync" — and 11 of them went on to `completed` with an output path. Every offset is at or near the −150 s window bound, and every one of them wrote a sidecar into the library.

| job | alignment line | align offset (s) | score | outcome | output written | bytes | `change=` |
|---|---|---|---|---|---|---|---|
| `7f0b9ce247214e7d9b5612eeca9ca2fb` | 3511 | 0.0 | -70725.0 | completed | — (nothing written) | unknown | already in sync (+0 ms offset) — nothing written |
| `89dec4ae6a514918b03a8755e582fdaf` | 3559 | -121.88 | -3481.721 | completed | `The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.SYNCED.srt` | 36230 | unknown |
| `14c87ee5a5ec46c29ed6f87db09cea4b` | 3617 | -123.85 | -36522.0 | completed | `The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.SYNCED.srt` | 39853 | unknown |
| `6f32e35558f3411b961ee34d931e0899` | 3631 | -144.38 | -41855.162 | completed | `The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.SYNCED.srt` | 41293 | unknown |
| `c35bf99dbbb54cc8be0b2a79b0c7e36d` | 5763 | -149.99 | -14333.68 | completed | `The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.SYNCED.ron.srt` | 37903 | unknown |
| `3a8fbe3d24194ae9bf444ff3064beb65` | 8695 | -149.99 | -41642.0 | completed | `Solsidan.S09E01.SYNCED.srt` | 98 | only 1 cue in a 22-minute file — looks like a forced/signs track, not the full subtitle |
| `155dd60e4ded4ed287bd7da861cb4081` | 8704 | -143.8 | -60411.0 | completed | `Solsidan.S09E03.SYNCED.srt` | 124 | only 2 cues in a 22-minute file — looks like a forced/signs track, not the full subtitle |
| `d4d768364d14408a8e8f5650fa7dba26` | 11350 | -121.88 | -3481.721 | completed | `The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.SYNCED.srt` | 36230 | unknown |
| `0e1392c9c28d41a2b9eec230fd371526` | 11397 | -139.18 | -2685.04 | completed | `The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.SYNCED.srt` | 36886 | unknown |
| `ba3b459ab20b47448296c0b4d8c0525f` | 11929 | -123.74 | -37612.56 | completed | `The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.SYNCED.srt` | 40550 | unknown |
| `a38c80abd34345d0aeeb73b58132d97c` | 11958 | -123.85 | -36522.0 | completed | `The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.SYNCED.srt` | 39853 | unknown |
| `c5fe02743cf14a689c8503de0511bff8` | 11973 | -137.84 | -29597.32 | completed | `The Unlikely Murderer - S01E04 - Episode 4 WEBDL-1080p.SYNCED.srt` | 49120 | unknown |
| `7e13311e4e214c9a95c1aedcced2e987` | 12097 | 0.0 | -70725.0 | completed | — (nothing written) | unknown | already in sync (+0 ms offset) — nothing written |

Evidence lines, verbatim, for two of them (the highest and the lowest score):

```
2026-09-18 12:20:03.959Z INFO  [3a8fbe3d24194ae9bf444ff3064beb65] ffsubsync alignment: score=−41642 offset=−149,990 s against reference subtitle s:0 - the engine itself calls a negative score an unsuccessful sync
2026-09-18 12:20:03.969Z INFO  job 3a8fbe3d24194ae9bf444ff3064beb65 completed: mode=ultimate output=/media/synology/Syn-Serier/Solsidan/Solsidan S09/Solsidan.S09E01.SYNCED.srt bytes=98 change=only 1 cue in a 22-minute file — looks like a forced/signs track, not the full subtitle extraction=n/a
```

```
2026-09-18 11:46:52.824Z INFO  [89dec4ae6a514918b03a8755e582fdaf] ffsubsync alignment: score=−3481,721 offset=−121,880 s against reference subtitle s:1 - the engine itself calls a negative score an unsuccessful sync
2026-09-18 11:46:52.842Z INFO  job 89dec4ae6a514918b03a8755e582fdaf completed: mode=ultimate output=/media/synology/Syn-Serier/The Unlikely Murderer/Season 1/The Unlikely Murderer - S01E02 - Episode 2 WEBDL-1080p.SYNCED.srt bytes=36230 change=unknown extraction=n/a
```

The two that did **not** write are the `Spermageddon (2024)` pairs — score −70725 with offset 0,000 s against the audio, where the `change=` says `already in sync (+0 ms offset) — nothing written`. In other words: the refusal only happened because the offset was zero, not because the score was negative.

### 7.8 Framerate / rescale activity

- **643 jobs** carry a `[jobid] framerate:` line (713 framerate lines in total).
- **472 of them rescale** ("the subtitle's span is Nx the reference's (a framerate pair) — rescaling it onto the reference's time base before aligning"), **171 decline** ("the reference is the odd one out — no rescale; a shift from a reference this far off the video is refused as before").
- Ratio spread: 0,90x → 2,40x (226 distinct ratios). The bulk sits in 0,90–1,05x, i.e. the ordinary 24↔25 / 23,976↔24 / WEB-DL-vs-Bluray drift.
- **5 jobs stretch by more than 1,5× or less than 0,9×** — a ratio that is not any framerate pair, and the shape most likely to be a wrong reference:

| job | file | ratio(s) | verdict | line |
|---|---|---|---|---|
| `c0dbbbd9539c45c0a08ba44c49ec4e77` | American.Nightmare.2024.S01E02.1080p.WEB.h264-GP-TV-NLsubs.mkv | [2.4015] | completed | 3008 |
| `ad8cea7b036744218b6c436ec9191e06` | BoJack Horseman (2014) - S01E02 - BoJack Hates the Troops (1080p BluRay x265 Ghost).mkv | [2.3515] | completed | 3377 |
| `6840e36108a74f34ad06a0658e2f2b2a` | The Helicopter Heist - S01E02 - The White Whale WEBDL-1080p.mkv | [1.6243] | completed | 3846 |
| `cce71995f68c43afaf7d7ad7d2af0802` | American.Nightmare.2024.S01E02.1080p.WEB.h264-GP-TV-NLsubs.mkv | [2.4015] | completed | 10412 |
| `b92d2470a0344d6fb25327c6e2a165d1` | The Helicopter Heist - S01E02 - The White Whale WEBDL-1080p.mkv | [1.6243] | completed | 13275 |

- **28 jobs had a stretch tested against the film's own audio and the stretch did not hold** ("the stretch does NOT hold against the audio (Nx, N ms) — a different cut looks the likely answer"), and were aligned with offsets only; 6 jobs were written from a `stretched to N,Nx …` result. All 28 of the "did not hold" jobs, for inspection:

| job | file | align offset (s) | score | outcome | framerate line |
|---|---|---|---|---|---|
| `e907a1f2289b4c26b14b17e5da835995` | The Puppet Master - Hunting the Ultimate Conman - S01E02 - Chasing a Ghost WEBDL-1080p.SYNCED.zho.srt | 1.98 | 73583.949 | completed | 630 |
| `2b517ddeb716429f8be560f60fd6e51c` | The Helicopter Heist - S01E02 - The White Whale WEBDL-1080p.SYNCED.zho.srt | 0.25 | 197050.08 | completed | 2069 |
| `0d8a3d7ed3c243f98c4d0d6cffd074fe` | The Helicopter Heist - S01E03 - The Devil Is in the Details WEBDL-1080p.SYNCED.zho.srt | -1.47 | 135149.72 | completed | 2076 |
| `85f136dedd0140e5a0b863020f554709` | The Helicopter Heist - S01E07 - The Hunt WEBDL-1080p.mkv | -0.18 | 196837.88 | completed | 2151 |
| `b583edcf099640288635f2f89be7d23b` | The White Lotus (2021) - S02E05 - That's Amore (1080p HMAX WEB-DL x265 Ghost).mkv | 0.49 | 202421.84 | completed | 2298 |
| `aa6a8a893e754d90b9b306e443e34508` | The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv | -5.12 | 139684.36 | completed | 2452 |
| `debd6b7dc1604e5baa0f1016910bf870` | Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv | -0.54 | 148768.88 | completed | 3504 |
| `ba63b38277df4581ab76b609af2fe945` | Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv | -0.53 | 154636.4 | completed | 3591 |
| `41dd60b84e7745e6b74f52633c543ad7` | The Helicopter Heist - S01E03 - The Devil Is in the Details WEBDL-1080p.mkv | -1.29 | 135825.56 | completed | 3855 |
| `88260220823a406281a747c97a5c8013` | Gosta.s01e01.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | -0.51 | 155595.0 | completed | 4523 |
| `827407effa274d71aa09f351d635ef11` | Gosta.s01e07.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | 2.83 | 118701.0 | completed | 4644 |
| `2fd99c6d1157485b892990c1303d1359` | Paradise.Is.Burning.2023.1080p.AMZN.WEB-DL.DDP5.1.H.264-MADSKY.mkv | 0.97 | 557949.0 | completed | 5062 |
| `11e6af40523f4afaa247a068021fd70c` | The Puppet Master - Hunting the Ultimate Conman - S01E02 - Chasing a Ghost WEBDL-1080p.mkv | 0.95 | 104828.896 | completed | 5091 |
| `ba9ee475042a40b29e09ff32412491bc` | The Puppet Master - Hunting the Ultimate Conman - S01E02 - Chasing a Ghost WEBDL-1080p.mkv | 0.95 | 103844.921 | completed | 5122 |
| `3e9e9f5a844e44aba7e929b4127cb166` | The Helicopter Heist - S01E07 - The Hunt WEBDL-1080p.mkv | -1.74 | 159816.44 | completed | 6897 |
| `21bf925582834fa085f305fbbe19ef31` | The White Lotus (2021) - S02E05 - That's Amore (1080p HMAX WEB-DL x265 Ghost).mkv | 0.38 | 209991.88 | completed | 8867 |
| `af70e4d801cc4df6acecca7fdba8af57` | The White Lotus (2021) - S02E06 - Abductions (1080p HMAX WEB-DL x265 Ghost).mkv | -2.74 | 153415.28 | completed | 8895 |
| `ffb11b7199274641960943e07ba4d310` | The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv | -5.15 | 139492.36 | completed | 9026 |
| `3b311a9838fc4e2f87560c8d5f161639` | The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv | -4.99 | 137929.48 | completed | 9041 |
| `7c8a35f82ddd4392a2683288408d6896` | Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv | -0.53 | 153851.12 | completed | 9169 |
| `cf955867af634d4aa6b2414de2d472b9` | The White Lotus (2021) - S03E07 - Killer Instincts (1080p HULU WEB-DL x265 Ghost).mkv | -4.99 | 137929.48 | completed | 9530 |
| `219458230e9741318ea426483ff29abb` | Gosta.s01e01.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | -0.83 | 155594.0 | completed | 10699 |
| `3419da5d133441d98bcb0f1124ec9a83` | Gosta.s01e07.Swedish.1080p.Web-dl.DD5.1.x264-NDF.mkv | 2.73 | 118702.0 | completed | 10824 |
| `267edd18414c4a098b2fff7c2c0d36ec` | The Puppet Master - Hunting the Ultimate Conman - S01E02 - Chasing a Ghost WEBDL-1080p.mkv | 0.94 | 104814.551 | completed | 11451 |
| `9c3802fdd47e4dd8a211cb1022c730ee` | The Puppet Master - Hunting the Ultimate Conman - S01E02 - Chasing a Ghost WEBDL-1080p.mkv | 1.97 | 73577.277 | completed | 11480 |
| `d03e83e92e0845e28e68120657032e90` | Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv | -0.53 | 153622.64 | failed | 11724 |
| `77db6d829fd846838f32aaa78ae3fc83` | Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv | -0.54 | 148768.88 | completed | 11760 |
| `0ad9f1f11fa74ae4b9c5648f9f1f7689` | Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).mkv | -0.58 | 148068.08 | completed | 12109 |

- The 6 jobs written from a `stretched to N,Nx onto the reference's timeline` change (each one is a rescaled subtitle):

```
 3135  5a5d416f43f144de8a94970812e3e092  bytes=18755  stretched to 1,04388x onto the reference's timeline
 4689  7232ae588a90457aa2ce400d2acf12e3  bytes=20874  stretched to 1,04555x onto the reference's timeline
 4705  0820631bccf94143af388729238fb1bd  bytes=23850  stretched to 1,04334x onto the reference's timeline
 4721  1ca9317e04e14e648cdd5713cf7c409c  bytes=26179  stretched to 1,04243x onto the reference's timeline
 4726  cedb0fa9e49d4c0caa11ff26602f995c  bytes=25939  stretched to 1,03899x onto the reference's timeline
11229  c6abafc1a5ff47289be2a935b2e1192f  bytes=18755  stretched to 1,04388x onto the reference's timeline
```

## Did today's GUI/backend changes show up as trouble in the logs

The changes under review (per the project's own `knowledge/FIX_PLAN.md`, section "New findings this session (2026-09-18 — the GUI/UX pass)") are: right-click-only selection in the web page; the History list rewrite (`2d17e75`, the per-file results area removed); the run-box / queue-status rewrite (`ecd0f31`, `b0d6710`, and the nine-task "pending is not a failure" mapping); and the pending-task-not-a-failure fix, shipped with the pass as 2.0.63 and its follow-ups 2.0.64 / 2.0.66.

**What the log can and cannot see, first.** These are web-page (JS/CSS/DOM) and status-mapping changes; the plugin log records jobs, batches, extractions and process lifecycle. The log contains **zero** lines that mention the page at all:

```
$ for p in http 'GET /' 'POST /' subsync.js panel- 'SubSync/Active' 'SubSync/Batch'; do printf '%-16s %s\n' "$p" "$(grep -c "$p" /tmp/logwork/snapshot.log)"; done
http             0
GET /            0
POST /           0
subsync.js       0
panel-           0
SubSync/Active   0
SubSync/Batch    0
```
So: a right-click-only selection cannot be observed in this log at all — not its absence, not its behaviour. What the log *can* show is the work those clicks produced, and the process/queue lifecycle around it.

**Restarts.** 8 `startup: version=` lines and 6 `teardown:` lines. Every startup is a version change or a reload of a version already deployed, all on the same worker settings:

| line | startup (UTC) | version | previous log line |
|---|---|---|---|
| 2504 | 2026-09-18 04:16:50.720Z | 2.0.63.0 | 2026-09-18 04:16:46.445Z |
| 2508 | 2026-09-18 04:22:47.163Z | 2.0.63.0 | 2026-09-18 04:22:39.926Z |
| 2510 | 2026-09-18 04:23:00.370Z | 2.0.63.0 | 2026-09-18 04:22:48.044Z |
| 2513 | 2026-09-18 11:42:09.618Z | 2.0.64.0 | 2026-09-18 11:42:06.103Z |
| 2516 | 2026-09-18 11:42:32.433Z | 2.0.64.0 | 2026-09-18 11:42:31.985Z |
| 9074 | 2026-09-18 13:58:14.610Z | 2.0.66.0 | 2026-09-18 13:58:11.654Z |
| 9077 | 2026-09-18 14:00:42.203Z | 2.0.66.0 | 2026-09-18 14:00:41.730Z |
| 9606 | 2026-09-18 21:50:24.866Z | 2.0.66.0 | 2026-09-18 14:25:25.374Z |

- **6 of the 8 startups have a `teardown:` within 4 seconds before them** (6 teardowns, all reporting `tracked=0 … aliveAfter=0 … trackedLeft=0`), i.e. a clean unload: no child process and no job was left behind. This is a deployment pattern (2.0.63 → 2.0.64 → 2.0.66 are today's builds), not a crash-restart loop.
- **Two startups have no teardown before them**: line 2510 (2026-09-18 04:23:00.370Z, 13 s after the previous startup with no unload in between) and line 9606 (2026-09-18 21:50:24.866Z, after 7 h 25 min of a silent log). The second one is the interesting one — the plugin was re-initialised without logging an unload. Severity low; what would settle whether it was an ungraceful stop or a logging gap: Jellyfin's own log around 21:50Z, and whether `teardown:` is reached on that path.
- Each of the 8 startups logs `batch history: restored 8 batch(es), 773 task(s) from /config/data/data/subsync/state/batch-history.json` — the identical
  text all 8 times, including the restarts at 13:58 and 14:00 (after the 11:44 batch had finished) and at 21:50 (after both of today's batches had finished). See new issue N10.

**Did any job go missing or vanish?** No — and this is measured, not asserted:
- batch `b76b77e3…` (created 11:44:13, label `Selection (285 files)`, 459 tasks): 459 terminal lines, no task without one; batch `cd29cd12…` (14:16, 35 tasks): 35 terminal lines. Both are complete.
- 1268 job ids were queued in this file; **none is queued twice**, and **no job ran the engine twice** (`duplicate_queued_job_ids = 0, duplicate_engine_runs_per_job = 0`), so there is no "work restarted under a new id" signature either.
- The 493 tasks with no terminal line are the tail of the run that was still going at the snapshot edge (see "Sync outcomes").
- The 5 engine runs with no terminal line all started in the last 5½ minutes of the file (00:15:13, 00:16:23, 00:19:38, 00:19:48, 00:20:10) — in flight, not vanished.
- No cancellation happened at all: 0 lines matching `Cancelled` / `killed by the user` / `stopped (`, and the only `extract lane: stopped` lines (6) immediately precede teardowns.

**Did the run-box / queue-status rewrite show up as trouble?** No contradiction is visible, and the queue lines look healthy, but the two families are logged on different cadences (88 `queue:` lines against 956 `dispatch:` lines) so they cannot be cross-checked instant by instant. What is visible: the `queue:` line always agrees with the run's own progress (at 00:20:13 it reports `488 queued, 5 running` while the batch still had 493 tasks without a terminal line), and the `dispatch:` line's per-batch counters reconcile exactly with the job tallies.

**Did the pending-task-not-a-failure fix show up?** The log is consistent with it and cannot verify the paint. The strongest supporting shape: batch `8b8dd2d6…` held 493 not-yet-started tasks for the last 10 minutes of the file, and the log records **no** failure for any of them — the batch's only failures are the 6 real ones, each with its own ERROR line and stack trace. Before that fix, those 493 rows were drawn `Failed` in red in History. The log cannot see the colour; what would settle it is a browser probe of `GET /SubSync/Batch/8b8dd2d6274c4d3187c62b887b0170a9` mid-run (the project's `tests/gui/priority4-colour.js` is that probe).

**One GUI-visible number the log does contradict.** All three page-created batches carry the same label and wildly different sizes:

```
3000 | 2026-09-18 11:44:13.293Z INFO  batch b76b77e3edb5450999bc5d8e207cb679 queued: tasks=459 mode=auto label='Selection (285 files)' totalMs=1592 slowestTaskMs=233 (index 176: gap=−2 ms; item=0 ms, sources=2 ms, settings=0 ms, log=0 ms, total=233 ms)
9119 | 2026-09-18 14:16:13.438Z INFO  batch cd29cd12a01e4be89018d3b9ae6751b8 queued: tasks=35 mode=auto label='Selection (285 files)' totalMs=166 slowestTaskMs=36 (index 13: gap=−3 ms; item=0 ms, sources=2 ms, settings=0 ms, log=0 ms, total=36 ms)
10405 | 2026-09-19 00:10:12.715Z INFO  batch 8b8dd2d6274c4d3187c62b887b0170a9 queued: tasks=772 mode=auto label='Selection (285 files)' totalMs=2455 slowestTaskMs=245 (index 188: gap=−2 ms; item=0 ms, sources=2 ms, settings=0 ms, log=0 ms, total=245 ms)
```

`Selection (285 files)` on a 459-task/298-file batch, on a 35-task/35-file batch, and on a 772-task/339-file batch. The label is what History shows as the name of a run, so a user reading History cannot tell these three runs apart from the label. Severity low(cosmetic/label), but it lands exactly in the rewritten area. What would settle whether the label or the task set is the stale half: the `/SubSync/Batches` response for those ids, which this log does not contain.

**Standalone jobs.** Two jobs today carry `batch=(standalone)` — the single-item case that used to be invisible in History (G1 in the fix plan): job `658cf543…` (queued 14:21:36, False Trail) and `5b5ffb09…` (14:25:21, the same file, a second subtitle). Both completed and wrote files. The log still prints `batch=(standalone)`, which is the same shape G1 described; whether they *appear* in History now cannot be determined from the log.

**Summary of section 8:** the logs show **no error, no missing job and no vanished run attributable to today's GUI/backend changes**. They do show two un-teardowned startups (one of them possibly the moment the changes were deployed) and one GUI-area number that disagrees with the label shown to the user. The right-click-only selection and the History/run-box rendering are invisible to this log by construction — nothing about them can be confirmed or refuted from it.

## What is working

Stated plainly, with the numbers that back each line:

- **The day's work is dominated by correct restraint.** 791 of 976 completed jobs were already in sync and **wrote nothing** (`change=already in sync (+0 ms offset) — nothing written`). 185 files were written, and of those 156 report an explicit offset in `change=`, with a median of **80 ms** and a Q1–Q3 of 40.0–292.5 ms — millisecond corrections, which is what a correct sync looks like.
- **The engine never failed to run:** 1000 `ffsubsync start:` lines and 1000 `ffsubsync exit=` lines, **all 1000 with `exit=0`**. Engine time per run: min 934 ms, median 1976 ms, max 1054174 ms; 5064.4 s (≈ 84 min) of engine time in the day. The longest single run is 1054174 ms on job `7fe98ec4aed34ffe84de9316c37dd9a4` (a `reference: method=audio` job — the file's own analysis).
- **Nothing was cancelled, and nothing leaked:** 0 cancellations; 6 `teardown:` lines, every one of them `tracked=0 stopped=0 killedDirect=0 exitedDirect=0 aliveAfter=0 … trackedLeft=0`; 6 `extract lane: stopped` lines only ever immediately before a teardown.
- **The extraction cache is carrying the load:** 745 of 976 jobs were served from the extracted-subtitle cache with **no read at all**, 12 did a real index read, 219 needed no extraction. Reference resolution likewise: cache 595 lines against 29 cue-index reads.
- **Every batch that finished is fully accounted for:** batch `b76b77e3…` 459/459 tasks terminal, `cd29cd12…` 35/35, both with 0 tasks missing; the two `batch=(standalone)` jobs both completed. No job id was queued twice and no engine was run twice for one job.
- **The confusing cases are handled out loud, not silently:** 38 times a job logged `the reference subtitle s:N is not the same cut … — discarding that track as a ruler and aligning …` and re-aligned against the audio instead (56 jobs resolved their reference from the audio in all, 5 of them from the speech cache); 472 jobs were rescale-corrected and 171 correctly declined a rescale; 28 jobs had a stretch tested against the film's own audio and the stretch was rejected before anything was written.
- **A refusal is a refusal:** the single REFUSED job says exactly why and wrote nothing; the 14 UNVERIFIED jobs wrote nothing and said why; 2 of the 10 failures deliberately *threw away* a partial extraction rather than accept it (`the 81 cue(s) it produced are only part of the track - discarded the partial extraction (5567 bytes)` and the same for 85 cues / 5649 bytes). Those are the right calls.
- **The read accounting is honest about its own misses:** 879 `extract plan:` lines, 20 of them flagged as a WARN miss, plus 427 `extract lane:` summaries of which 3 report `ok=False` rather than a fake success. 10 `extract plan … [bound, not verified]` markers are printed on the plans that are bounds rather than estimates.
- **A 459-task batch ran end to end in about 42 minutes**: batch `b76b77e3…` first appears at 2026-09-18 11:44:11.727Z and its last event is 2026-09-18 12:26:11.540Z — 459 tasks across 298 files, of which 452 completed, 3 failed, 3 UNVERIFIED and 1 REFUSED. The storage is the limit, not the plugin: the `this walk moved` lines run 12,3–1561,3 MB/s (median 136,3 MB/s), and the same volume that reports 3090,8 ms per read is the one whose rows say "thrashing".

## New issues found

Read-only pass — nothing here has been fixed, and nothing outside the two deliverables was touched. Severity uses this project's own rule (`knowledge/FIX_PLAN.md`): **high** = a wrong result, data loss or an unauthorised action reaching the library; **medium** = a visible malfunction, wasted work or a misleading report with a workaround; **low** = cosmetics, robustness, hygiene. Evidence is quoted from this run's log with line numbers.

| # | issue | severity | evidence |
|---|---|---|---|
| **N1** | **A job whose alignment *failed* still wrote a synced subtitle into the library.** 13 jobs' last `ffsubsync alignment:` line has a negative score — the plugin's own log text for it is "the engine itself calls a negative score an unsuccessful sync" — and **11 of them went on to `completed` with an output path** (Solsidan S09E01/E03, The Unlikely Murderer S01E02/S01E04 ×5). Every offset is at or near the −150 s window bound (−121,88 s … −149,99 s). The two that refused did so because the offset happened to be 0,000 s, not because the score was negative. 9 of the written ones report `change=unknown` and the 2 Solsidan ones say `change=only 1 cue in a 22-minute file — looks like a forced/signs track, not the full subtitle` — i.e. the plugin knew the track was a signs track and wrote it anyway. | **high** (a wrong file reaches the library and Jellyfin will offer it; the earlier S3 rule covered the subtitle-*reference* case, this is the residual) | lines 3559, 3617, 3631, 5763, 8695, 8704, 11350, 11397, 11929, 11958, 11973 (table 7.7) |
| **N2** | **`change=` says `unknown` for written files** — 16 completion lines that wrote a file report no offset at all, so the run box and the History row have nothing to say about what the sync did. | **medium** (misleading report; the user cannot tell a 10 ms correction from a 150 s shift) | `change_kinds` in the JSON; e.g. line 3618, 11974 |
| **N3** | **One library folder is not writable, so every job that targets it fails** — 7 ERROR lines, all `Cannot write the synced subtitle to '/media/Serier/Black Mirror/Black.Mirror.2011.S05.1080p.NF.WEB-DL.H.265.SDR.DDP.5.1.English-HONE': the Jellyfin user has no write permission there (the folder may also be mounted read-only). Nothing was changed and the original subtitle is untouched.` Two bursts: 11:57 (2 jobs) and 00:14–00:15 (5 jobs), on two different items (`6df9163a…`, `3a7b4d68…`, `67f0b7bb…`). The plugin handles it correctly; the folder does not. | **high** (every sync of that item is a failed task; it is an environment fix, not a code fix) | lines 4790, 4818, 11845, 11875, 11907, 11941, 12008 (+ the exception line 4791) |
| **N4** | **The queue lock is held for up to 30,5 seconds** — 167 `queue lock slow:` lines, 50 of them over 1 s, holder `pump-plan-outside-lock` in 165 of them (the name says the planning was meant to be outside the lock). Worst case **30 483 ms**. The first `dispatch:` line of a batch can therefore lag the click by half a minute. | **medium** (wasted wall clock on every batch start; the run-box's first numbers arrive late) | lines with `queue lock slow:` (167) |
| **N5** | **The cue-index plan for the `solsidan.s03*.bluray-prince` shape is off by 60–72× on bytes and ~19–21× on reads** — 20 WARN `extract plan: … - this pass missed its own prediction` lines, e.g. line 7030 `expected 0.62 MB/518 read(s) (102909.8 ms), actual 38.88 MB/9861 read(s) (2980720.3 ms) - bytes 63.20x, reads 19.04x`, and the same set repeats at 12:18 in a second round. Each miss is up to 40 MB and ~10 000 reads of avoidable work per episode (10 episodes). | **medium** (wasted reads on exactly the storage that is slowest) | lines 7030, 7144, 7209, 7244, 7265, 7299, 7307, 7316, 8079, 8105, 8337, 8480, 8494, 8508, 8522, 8535, 8549, 8563, 8576, 8611 |
| **N6** | **A fetch that does not cover the read it was made for, 20 times** — `extract: <file> read X MB past the fetch (a fetched range that did not cover the read it was made for)`. Values are small (0,00–0,17 MB) but the mechanism is a bookkeeping miss, and it is concentrated on the same Solsidan set (17 of the 20) plus two American Nightmare episodes. | **low** (correctness of the accounting; a read happens twice or outside the plan) | lines 773, 7213, 7256, 7276, 7303, 7311, 8091, 8107, 8345, 8482, 8496, 8510, 8524, 8537, 8551, 8565, 8578, 8613, 9808, 9822 |
| **N7** | **`Thunder in My Heart - S01E03` cannot be extracted from its container and fails twice** — 2 ERROR jobs (stream 9 at 12:13:51, stream 4 at 14:18:47), 2 `extract lane … ok=False, reason=no subtitle blocks found` lines, 2 `extract fallback to ffmpeg` lines whose metadata scan found 0 cue points, and 2 WARN `extract: rejected … ffmpeg read a partial file` — the file is genuinely unextractable by both routes. Nothing wrong was written, but the same broken work is repeated on every run. | **medium** (a permanently failing item that costs two full analysis attempts per run) | lines 7763, 7611, 8093, 8094, 9510, 9398, 9557, 9558 |
| **N8** | **A rescale ratio that is not any framerate pair: 1,62x / 2,35x / 2,40x on 5 jobs** — `the subtitle's span is Nx the reference's (a framerate pair) — rescaling it onto the reference's time base`. A 2,40x "pair" is not 24↔25 or 23,976↔24; it is most likely a wrong cut being stretched. 5 jobs in total were stretched by more than 1,5× or less than 0,9×. | **medium** (a wrong rescale produces a subtitled-but-drifting file; two of these jobs are the same BoJack episode and two are the same American Nightmare episode — the same wrong answer computed twice) | lines from `framerate.extreme_ratio_jobs`: jobs `c0dbbbd9…`, `cce71995…` (2,4015x), `ad8cea7b…` (2,3515x), `6840e361…`, `b92d2470…` (1,6243x) |
| **N9** | **The batch label does not describe the batch** — three page-created batches all labelled `Selection (285 files)` carried 459 tasks across 298 files, 35 tasks across 35 files, and 772 tasks across 339 files. History shows the label as the name of the run, so the user cannot tell these three apart, and the one label is wrong for at least two of them. | **low** (cosmetic/naming, but inside the area rewritten today) | lines 3000, 9119, 10405 |
| **N10** | **`batch history: restored 8 batch(es), 773 task(s)` is byte-identical on all 8 startups of the day, including the two after today's batches completed (13:58, 14:00) and the one at 21:50** — so whatever the History list is rebuilt from had not changed across a day in which two batches were created and 488 jobs completed (452 + 34 + the 2 standalone). Either the state file is rewritten on a path that never ran, or the restore line counts something else. | **medium** (the History rewrite's data source; a restarted server may show yesterday's list) | lines 2505, 2509, 2511, 2514, 2517, 9075, 9078, 9607 |
| **N11** | **Two startups were not preceded by a teardown** — line 2510 (04:23:00.370Z, 13 s after the previous startup with no unload between) and line 9606 (21:50:24.866Z, after 7 h 25 min of silence). The other six unloads all report `aliveAfter=0 trackedLeft=0`, so this is not a leak; it is the unload of those two that has no record. | **low** (robustness; if the unload did run, the log misses it) | lines 2503–2517, 9073–9078, 9606 |
| **N12** | **A sidecar write raced a reader and lost** — `System.IO.IOException: The process cannot access the file '…Arcane (2021) - S02E08 - Killing Is a Cycle (1080p BluRay x265 Ghost).SYNCED.srt' because it is being used by another process.` at 00:15:28, while sibling jobs of the same file wrote their own sidecars at 00:15:28 and 00:17:05. | **low** (one failed task, retried on the next run; the file is shared between parallel jobs) | line 12101 + exception line 12102 |
| **N13** | **`/media|/dev/sda2` is described as thrashing at 3090,8 ms per read**, and 48 of the 147 `walk ceiling: holding …` lines say "thrashing". The plugin throttles correctly (it holds the volume at 1 concurrent read), but every job touching that volume pays 3 seconds per read. | **medium** (storage, not code — a 3 s read makes the whole volume the run's clock) | the `walk ceiling:` lines; e.g. line 59 `holding /media|/dev/sda2 at 1 concurrent media read(s) - this volume measured 3090,8 ms per read, over 3 read(s), which is thrashing` |
| **N14** | **Not determinable from the log, stated so it is not mistaken for clean:** whether the 5 engine runs without a terminal line ended well (they are in flight at the snapshot edge); whether the right-click-only selection, the History rendering and the pending-row colour behave (no page line exists in this log at all); whether `batch-history.json` on disk matches the restore line (N10); and whether those two startups tore down cleanly (N11). What would settle each: the next tail of the live log, a browser probe of `/SubSync/Batches` and `/SubSync/Batch/{id}`, the file `/config/data/data/subsync/state/batch-history.json`, and Jellyfin's own log around 21:50Z. | — | — |

## Commands used to produce each number

Everything is measured from a frozen copy so the numbers do not move under the analysis (the live log is still being appended to):

```
$ cp /subsync-logs/subsync.log /tmp/logwork/snapshot.log && md5sum /tmp/logwork/snapshot.log
7694cdd7a229f14e99182a74ed9d41c6  /tmp/logwork/snapshot.log
$ wc -l /tmp/logwork/snapshot.log
13394 /tmp/logwork/snapshot.log
```

All counts come from one reproducible parser (kept in the repo so the numbers can be re-derived):

```
$ python3 tests/backend/analyse_log_2026_09_18.py \
      --log /tmp/logwork/snapshot.log \
      --json tests/backend/log-analysis-2026-09-18.json \
      --dumpdir /tmp/logwork
```

| number(s) | command (all run against `/tmp/logwork/snapshot.log`) | JSON key holding it |
|---|---|---|
| lines, bytes, first/last timestamp, per-day counts, monotonic, window edge | `wc -l` / `wc -c` + the parser's `log` block | `log` |
| WARN and ERROR counts and their line numbers | `grep -nE ' (WARN|ERROR) ' /tmp/logwork/snapshot.log` | `warn_error` |
| `[bound, not verified]` occurrences | `grep -n '\[bound, not verified\]' /tmp/logwork/snapshot.log` (`grep -c` → 10) | `bound_not_verified` |
| `extraction=` split | `grep -c 'extraction=' ` + the parser's counter over completion lines | `extraction` |
| reference methods (job-level and file-level) | the parser's counters over `reference: method=` / `extract: method=` | `reference_methods` |
| `this walk moved … MB … = … MB/s` values | `grep -n 'this walk moved' /tmp/logwork/snapshot.log` | `read_byte_counts.walk_moved_values` |
| `bytes=` values on completions | `grep -o 'bytes=[^ ]*' | sort | uniq -c` | `read_byte_counts.bytes_values` |
| `extract plan:` expected/actual families | the parser's `extract plan:` regex | `read_byte_counts.extract_plan_*` |
| outcome counts (completed/UNVERIFIED/REFUSED/failed/queued) | `grep -oE 'job [0-9a-f]{32} (completed|UNVERIFIED|REFUSED|failed)' … | sort | uniq -c` and `grep -c 'queued: job='` | `sync_outcomes` |
| absence of `Done (...)`-style batch totals | `grep -c 'Done (' → 0`; `grep -icE 'batch .*(done|finished|complete)' → 0` | `sync_outcomes.done_paren_lines` |
| absence of cancellations | `grep -icE '\bCancelled\b|killed by the user|stopped \(' → 0` | `sync_outcomes.cancelled_lines` |
| per-batch task totals and reconciliation | `grep -n 'batch [0-9a-f]\{32\} queued: tasks='` + the parser's per-batch tally | `per_batch`, `per_batch_detail` |
| pending / orphan / in-flight jobs | the parser's job model (queued vs terminal ids) | `run_accounting` |
| align offsets and score families | the parser's `ffsubsync alignment:` regex + Tukey fences in `tukey()` | `offsets` |
| written offsets (from `change=`) | the parser's completion-line regex + `change_offset_ms` | `written_offsets` |
| negative-score-final jobs that wrote a file | the parser's last-alignment-per-job pass | `negative_score_final` |
| framerate / rescale families and ratios | the parser's `framerate:` regex | `framerate` |
| restarts, teardowns, batch-history restores, idleness | `grep -n 'startup: version='` / `teardown:` / `batch history: restored` | `queue_and_process`, `idle_before_startup` |
| queue-lock and walk-ceiling pressure | `grep -n 'queue lock slow:'` / `grep -n 'walk ceiling:'` | `queue_lock_slow`, `walk_ceiling` |
| engine exit codes and runtimes | `grep -oE 'ffsubsync exit=\S+ after [0-9]+ ms'` | `engine_exit_codes`, `engine_run_ms` |
| the exact log text of any line quoted above | `sed -n '<n>p' /tmp/logwork/snapshot.log` for the line number given | the `*_lines` / `rows` arrays |

The raw per-job dumps the parser also writes (not deliverables, kept for inspection): `/tmp/logwork/jobs.json`, `warn.json`, `error.json`, `offsets_all.json`, `plans.json`, `pending_jobs.json`, `dispatch.json`, `framerate_msgs.json`.

Full command list, as run:

```
# snapshot
$ cp /subsync-logs/subsync.log /tmp/logwork/snapshot.log && md5sum /tmp/logwork/snapshot.log
# run_parser
$ python3 tests/backend/analyse_log_2026_09_18.py --log /tmp/logwork/snapshot.log --json tests/backend/log-analysis-2026-09-18.json --dumpdir /tmp/logwork
# line_count
$ wc -l /tmp/logwork/snapshot.log
# warn_error
$ grep -nE ' (WARN|ERROR) ' /tmp/logwork/snapshot.log
# bound
$ grep -n '\[bound, not verified\]' /tmp/logwork/snapshot.log
# done_paren
$ grep -c 'Done (' /tmp/logwork/snapshot.log
# walk_moved
$ grep -n 'this walk moved' /tmp/logwork/snapshot.log
# bytes
$ grep -o 'bytes=[^ ]*' /tmp/logwork/snapshot.log | sort | uniq -c
# outcomes
$ grep -oE 'job [0-9a-f]{32} (completed|UNVERIFIED|REFUSED|failed)' /tmp/logwork/snapshot.log | awk '{print $3}' | sort | uniq -c
# startups
$ grep -n 'startup: version=' /tmp/logwork/snapshot.log
$ grep -n '\[bound, not verified\]' /tmp/logwork/snapshot.log
$ grep -c '\[bound, not verified\]' /tmp/logwork/snapshot.log
$ grep -o 'bytes=[^ ]*' /tmp/logwork/snapshot.log | sort | uniq -c | sort -rn | head
$ grep -oE 'job [0-9a-f]{32} (completed|UNVERIFIED|REFUSED|failed)' /tmp/logwork/snapshot.log | awk '{print $3}' | sort | uniq -c
$ grep -c 'Done (' /tmp/logwork/snapshot.log
$ grep -icE '\bCancelled\b|killed by the user|stopped \(' /tmp/logwork/snapshot.log
$ grep -c 'this walk moved' /tmp/logwork/snapshot.log
$ grep -c 'queue lock slow:' /tmp/logwork/snapshot.log
$ grep -c 'walk ceiling:' /tmp/logwork/snapshot.log
$ grep -cE 'ffsubsync exit=0 after' /tmp/logwork/snapshot.log
```

## Appendix — every `bytes=` and every `this walk moved` value, in full

The two families the section above summarises; the same rows are the JSON keys `read_byte_counts.bytes_values` and `read_byte_counts.walk_moved_values`.

### Every `bytes=` value on a completion line (185 numeric values; the other 791 completion lines say `bytes=unknown`)

```
     8  58889688e2aa43d0b814584cca911401  bytes=110828
    27  fb6edf859e8943e5b07557d9372b40c6  bytes=30172
    34  6016b14a155c42fc80b410d4de4e5a2b  bytes=35318
    45  167ac96f293a4c8ab4a62af88d2efa6d  bytes=30249
    58  dc172b4461a24a44b5fa5b073e74240d  bytes=35917
    80  cddd1cffc9cc4edf81907e19b911bc2c  bytes=36782
    90  6683c66a567741ebb92703141ca6b53a  bytes=27290
   111  4e1938ebad2949f38766e25da69ed06e  bytes=29330
   132  25d4f359d7d241bb82295af07c176fb3  bytes=31016
   168  8f87b6d9caf54768989acbdd727e68b9  bytes=28546
   188  429dd8169d66491db1c26167669ee981  bytes=30189
   209  5d3d7f29c11146329387b9b2ac028b1d  bytes=29067
   257  520220cb3f9f47c4b2672b774bbec080  bytes=31909
   278  14b31b0dddd44cdf8c0efd7828a8cb1e  bytes=31534
   300  88d872c92271458ca018e851956f8499  bytes=5012
   394  a04258b4c1084419ab30c12fce9a9e18  bytes=32468
   569  a7fbdf7c5453406196e9c60207138392  bytes=45636
   575  bb3e23474de74a12b86a16e7be1d8de3  bytes=48560
   651  de23418ba80a48ae975aa9652b4aedbc  bytes=78005
   896  e907a1f2289b4c26b14b17e5da835995  bytes=30744
   919  e452c703b6e74919b61c05262683f092  bytes=19296
  1019  dfc1b78610cd4d31a8b968c02ae3710d  bytes=12932
  1084  f4100be00ecd4ac09667866588d02d54  bytes=20815
  1088  bf30c49830cb4c22883a172dff235bb3  bytes=27060
  1112  30ba3cb2f6b643d880a30d3f15e14479  bytes=35107
  1344  f4026a289a824670ac437eb385d4500e  bytes=39707
  1514  3606c47360744ec9b1381ae254b0dc77  bytes=23914
  1532  7f1d05524ea9444282f3516b05e27b09  bytes=30669
  1542  e6cb0b065aef485b8dba4dd8d121870c  bytes=26943
  1559  cfd5e2208cc244fb84613865eb923dc2  bytes=35679
  1596  b58d2e60fd2144d29b7c502e590430ce  bytes=27055
  1604  4d2ba2db51ab4c1b83fa8be28b0bd851  bytes=33490
  1706  8da29fb4130448828f341dcdd39555c6  bytes=35959
  1858  5cd02ada5f48482f94e7cfb3f26e4cda  bytes=28445
  1866  e2118caff4c3440f84cbd8bcb8296891  bytes=35682
  2103  2b517ddeb716429f8be560f60fd6e51c  bytes=46641
  2137  0d8a3d7ed3c243f98c4d0d6cffd074fe  bytes=33036
  2246  e8572413da9c48dc97c21d3d59e9f638  bytes=42090
  2274  85f136dedd0140e5a0b863020f554709  bytes=30171
  2284  4eb295c5d42647be94a3c093e00ecc54  bytes=38040
  2293  02f81c349d1542bb8e6b1197878afb30  bytes=42189
  2320  b583edcf099640288635f2f89be7d23b  bytes=36952
  2330  ea3ea265c9ff43c791a4e802051cf961  bytes=35229
  2340  03f4aaa9228a4190b4d1a95a95bcd775  bytes=38292
  2358  06fc870d2ef8439b9ba5201a1a1c566b  bytes=46123
  2368  e870a9d2a9dd4784aa7eb64403aed4ff  bytes=44574
  2386  1d0a9dfade294570a09cf6ffcb592336  bytes=38848
  2394  a01c2789f0164b0f9378f41f490077a8  bytes=41423
  2412  c1e2de2c9da34c569383b22fc2b8794a  bytes=46378
  2421  6d7bb0cfb70442b68f4fb34af34ed392  bytes=39696
  2429  b688bc4d12e343eaa64fe299c64bac48  bytes=40532
  2439  f5dbdca1c4154d24abd0f88e526701d5  bytes=36846
  2447  84cf7cfafd4b4ebc8fa85fa05c841c0d  bytes=40423
  2467  aa6a8a893e754d90b9b306e443e34508  bytes=36251
  2475  bdd1e37de9da4b038389e3a41b1adcfc  bytes=40466
  3026  c0dbbbd9539c45c0a08ba44c49ec4e77  bytes=38812
  3093  ae66c480d6884aa68dd0a0906e3aea62  bytes=42995
  3135  5a5d416f43f144de8a94970812e3e092  bytes=18755
  3292  e401ab29927246628abbbb7bfb594448  bytes=61
  3357  dacafdca51124b87b1c837c1f249100e  bytes=65002
  3517  f370e7494ed545678f62e80bff42ac2c  bytes=10841
  3543  dfda1836e0b84d5d9d461542b5765969  bytes=42042
  3551  d8bec190948a48c09f921f33b19bada8  bytes=49518
  3560  89dec4ae6a514918b03a8755e582fdaf  bytes=36230
  3589  debd6b7dc1604e5baa0f1016910bf870  bytes=20373
  3605  63c9d45facf344499675c4e92fa4e23b  bytes=39038
  3618  14c87ee5a5ec46c29ed6f87db09cea4b  bytes=39853
  3625  ff1c2f6564164698836fe65d09cebfc7  bytes=40986
  3634  6f32e35558f3411b961ee34d931e0899  bytes=41293
  3675  ba63b38277df4581ab76b609af2fe945  bytes=24555
  3842  66110847a9154b4cb89504c640dc83ad  bytes=41541
  3851  6840e36108a74f34ad06a0658e2f2b2a  bytes=32554
  4016  c7336759e2864ef2a38c2504fa2341d5  bytes=67461
  4037  6265c565f9b84838811b86e86555cbc0  bytes=80241
  4162  41dd60b84e7745e6b74f52633c543ad7  bytes=25016
  4204  a4b96e714c324f4cbca7d45997bf0319  bytes=33836
  4229  06ea3a36ab134bb48cef70da4ba8f832  bytes=27795
  4312  b031527d3f55499e9f61944d0cf40292  bytes=16682
  4394  72744fabf04e4816a095b1d93cfb1344  bytes=29543
  4689  7232ae588a90457aa2ce400d2acf12e3  bytes=20874
  4705  0820631bccf94143af388729238fb1bd  bytes=23850
  4721  1ca9317e04e14e648cdd5713cf7c409c  bytes=26179
  4723  ad8cea7b036744218b6c436ec9191e06  bytes=24212
  4724  88260220823a406281a747c97a5c8013  bytes=24220
  4726  cedb0fa9e49d4c0caa11ff26602f995c  bytes=25939
  4727  827407effa274d71aa09f351d635ef11  bytes=28749
  4931  4019d141a97a4338a08bd6bda8ba39ba  bytes=49846
  5203  d0d0ba29877b4c9388561a116851767f  bytes=78117
  5365  11e6af40523f4afaa247a068021fd70c  bytes=26886
  5461  8a011ee650e940ac95143e3593a81ca2  bytes=28946
  5489  9e5e84819c1d4782864cd878c15aed86  bytes=33842
  5523  e32893f189b945caa2dea8cba5003618  bytes=32985
  5599  1db6d1fc563549719f615319ed8a5f0c  bytes=33807
  5673  e71951516d8c408db61a292baa34feea  bytes=26572
  5677  5871127747254cce8deecf507969f8a0  bytes=33465
  5708  338abb2b9db14e8ab691b924181218b2  bytes=34572
  5746  6ea2b727878c4f0f85d71b747c8294da  bytes=35439
  5765  c35bf99dbbb54cc8be0b2a79b0c7e36d  bytes=37903
  5799  67bc4e82f3d84567bd1785999106b01e  bytes=29795
  5808  f62f616e395241ebaafd9f2dfd5b710c  bytes=26915
  5844  aba65efc7bfb4420b7cf4aa88e022d23  bytes=34505
  5925  b57206e7e3d04d38a6b89a62a4b276f2  bytes=35330
  5941  2fd99c6d1157485b892990c1303d1359  bytes=43341
  5944  b6f13c883e1b4d2abeee2ddbd93e8221  bytes=36851
  5961  95212f57bb4f49169dbc47766da30db9  bytes=35623
  5970  b7fd31615e334f98ad99b3c3dbbbba57  bytes=35051
  5978  8959e9b6c6644f2a9ee53d5777338689  bytes=34327
  5995  cb1d5d5721bc455dbf4a92153204e7d5  bytes=36790
  6011  43c6e9222b6a429286999fcbb80018fb  bytes=36043
  6063  e160fae26a944a26981787878a3b4e1a  bytes=35585
  6114  5a22af9ce19f48b7b9e829a72fa7635d  bytes=36561
  6149  1a2b9a1a4572407bb4f7a0b3cdcbb788  bytes=6644
  6565  e3ad07e3a6394bfc96b492bb217d4ec7  bytes=31752
  6725  f7b8ab4348a54f739ef62e6b29c3152f  bytes=26475
  6781  9c7b3a5b540e4c4f83a2088efd678081  bytes=28871
  6803  233e31eb9b8c45e3a158354030039f13  bytes=32185
  6827  0db0cb373b2d4b49848d881a9469f8ed  bytes=35108
  7099  2ddc2f6283434f73862fdda30b11b9e2  bytes=35948
  7180  3e9e9f5a844e44aba7e929b4127cb166  bytes=35541
  7203  e633867f84374d33a8cbbb40eac438ec  bytes=19474
  7281  ccd21eda4afe42b383106c7fe4c49c98  bytes=58253
  8696  3a8fbe3d24194ae9bf444ff3064beb65  bytes=98
  8705  155dd60e4ded4ed287bd7da861cb4081  bytes=124
  8731  e913e30d6feb4cd4b9de9c017c527618  bytes=16756
  8740  9e09e6310d904e0b9d09321857d79d01  bytes=14154
  8760  274f33113db04bb882dc66120500c86c  bytes=116
  8787  a45ceccdbd034825af238c25642bbd13  bytes=150
  8813  2aae2b21091740219fce641694d4ff92  bytes=53847
  8830  43b60648eaa04c70a90733bc5d506773  bytes=50579
  8847  3c690b59cb0d41368e6566ec2e5a5f0b  bytes=48901
  8855  ebf91dc1bda04500bbe3e02a17b1e71d  bytes=367
  8872  0cce38be02114e139eac4fa4ac734a1b  bytes=53873
  8883  9c90d7ef422c42099db4b77b3a6c49da  bytes=46876
  8910  21bf925582834fa085f305fbbe19ef31  bytes=40053
  8927  7e80bd1cc44e49f0b2dc6f76c072d69a  bytes=49183
  8953  1754e40a011b4bcfb0cb93fc280f62a1  bytes=49848
  8987  8492696ea5dd4e6f8034254a7580439c  bytes=45059
  9007  3388fd9e13f84c03972b333867cb73f4  bytes=50054
  9022  4d6ac038730142859aac61204392102d  bytes=44190
  9035  3e5b8e162c11438bb70ff52ae90f936d  bytes=43696
  9051  3b311a9838fc4e2f87560c8d5f161639  bytes=44275
  9058  ffb11b7199274641960943e07ba4d310  bytes=38086
  9417  f97778e11e4c450296ad8aa141c244ad  bytes=21887
  9582  7c8a35f82ddd4392a2683288408d6896  bytes=23333
  9584  cf955867af634d4aa6b2414de2d472b9  bytes=41031
  9595  658cf5435ae140358e4e54f014b0aa2b  bytes=73010
  9605  5b5ffb09489449f68f0cf2ee380b98fc  bytes=73010
 10428  cce71995f68c43afaf7d7ad7d2af0802  bytes=38812
 10542  b077ba7e5fba4c37a72b2076aaf3c42d  bytes=42995
 10820  3e615acc7d1c4f37aa1277d9636f2447  bytes=20874
 10904  4cec52b2b2fa4cbbbba10541cc572580  bytes=62524
 10938  1037b11ac97d468d99e7aa484983b370  bytes=23850
 10979  2ad489446ca74426abb8706984658031  bytes=24801
 10987  f094db5567ec4681b3494f057698cd44  bytes=26179
 11000  08fed8a2e279470196ab726640ad6730  bytes=19605
 11025  f35acd4f02df42bba6fdbdee665ae4ad  bytes=25939
 11028  ed2aec15ed14488eb3ddaa854a1f4837  bytes=24801
 11132  105cd7b42b9f4d5f960343eaae2d9992  bytes=59801
 11180  0e9f0eacde0a48d298b4aa8adb38a9ce  bytes=44592
 11229  c6abafc1a5ff47289be2a935b2e1192f  bytes=18755
 11237  372a74c4d7b7494181f7eed0f0af558c  bytes=42042
 11351  d4d768364d14408a8e8f5650fa7dba26  bytes=36230
 11400  0e1392c9c28d41a2b9eec230fd371526  bytes=36886
 11517  295132b71c7246bab1dc0cb0eaa78b6e  bytes=16706
 11531  29abd22dba984f1b9502ff83da3e9e83  bytes=61
 11774  267edd18414c4a098b2fff7c2c0d36ec  bytes=26886
 11800  20e719c4973541649f28cb5bf9405e34  bytes=56060
 11834  cb0dd08064b249f8b741baa6fc7005a0  bytes=41252
 11867  871ff2acb6f2431e85375f04110c98c7  bytes=39038
 11930  ba3b459ab20b47448296c0b4d8c0525f  bytes=40550
 11959  a38c80abd34345d0aeeb73b58132d97c  bytes=39853
 11974  c5fe02743cf14a689c8503de0511bff8  bytes=49120
 12107  77db6d829fd846838f32aaa78ae3fc83  bytes=20373
 12204  8355eb46c6084dee845e900e833802c8  bytes=27716
 12223  1170914f61bd4c0b8b1ca549d694db54  bytes=26915
 12302  2dded1e35f814e0f865abbdb962d21ad  bytes=99583
 12305  0457506bd37e4aa5b45b3d7c03376281  bytes=65002
 12427  0ad9f1f11fa74ae4b9c5648f9f1f7689  bytes=23247
 12740  1bc9cec0d1624148aeef43a1428f05c2  bytes=10841
 12902  45d8d614ed124e59a07c6017bde287a2  bytes=67461
 13198  396722f3e44549dcb779c7562ec08194  bytes=67472
 13249  ef0df6046cf84a24b29238800e58db83  bytes=49021
 13264  5a1e258abf5548edbab82b65b80b6112  bytes=41541
 13284  b92d2470a0344d6fb25327c6e2a165d1  bytes=32554
 13325  29a392efe07f4569a8ea67f2dbf7e239  bytes=80241
```

### Every `this walk moved` line (17 lines)

```
999  2026-09-18 03:31:55.234Z INFO  [6e1542fc99db428d92edbb3e7ce55297] this walk moved 6662,9 MB of /Media/Movies/Min lilla syster/Min.lilla.syster.2015.1080p.BluRay.DD5.1..x264-NDF.mkv in 54,5 s = 128,3 MB/s, but it is not being used to judge that volume: 5 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
1549  2026-09-18 03:32:39.923Z INFO  [1ba973153d054a30b50329bf8d78ef18] this walk moved 1907,4 MB of /Media/TV Shows/The Trio/Season 1/The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv in 10,6 s = 188,0 MB/s, but it is not being used to judge that volume: 4 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
2051  2026-09-18 03:40:13.395Z INFO  [9e9b13c817b54e93846655f042577eda] this walk moved 2631,6 MB of /media/synology/Syn-Serier/The Unlikely Murderer/Season 1/The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv in 2,0 s = 1377,0 MB/s, but it is not being used to judge that volume: 2 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
2099  2026-09-18 03:44:14.971Z INFO  [52447ad90bf440819dae0e5cfef29e27] this walk moved 8142,4 MB of /media/synology2/Syn2-Filmer/The Ugly Stepsister (2025)/The Ugly Stepsister (2025) Bluray-1080p.mkv in 695,2 s = 12,3 MB/s, but it is not being used to judge that volume: 2 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
2269  2026-09-18 03:48:39.084Z INFO  [7fe98ec4aed34ffe84de9316c37dd9a4] this walk moved 13087,1 MB of /media/synology2/Syn2-Filmer/Kronjuvelerna/Kronjuvelerna.2011.Swedish.1080p.BluRay.DTS-HD.x264-NDF.mkv in 1054,2 s = 13,0 MB/s, but it is not being used to judge that volume: 2 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
2498  2026-09-18 03:58:59.870Z INFO  [1716a26c5f6e4411bba5776675ef8a0e] this walk moved 8562,3 MB of /media/synology/Syn-Filmer/The Lighthouse (2019)/The Lighthouse (2019) Bluray-1080p.mkv in 334,6 s = 26,8 MB/s - the ceiling for that volume is 2 (this volume measured 13,8 ms per read, which is storage-bound)
3512  2026-09-18 11:46:26.106Z INFO  [7f0b9ce247214e7d9b5612eeca9ca2fb] this walk moved 1362,5 MB of /Media/Movies/Spermageddon (2024)/Spermageddon (2024) Bluray-1080p.mp4 in 34,0 s = 42,0 MB/s, but it is not being used to judge that volume: 4 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
3516  2026-09-18 11:46:27.117Z INFO  [f370e7494ed545678f62e80bff42ac2c] this walk moved 1907,4 MB of /Media/TV Shows/The Trio/Season 1/The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv in 14,7 s = 136,3 MB/s, but it is not being used to judge that volume: 4 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
6521  2026-09-18 12:01:55.936Z INFO  [3232d380462643f483af51b33f112399] this walk moved 2631,6 MB of /media/synology/Syn-Serier/The Unlikely Murderer/Season 1/The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv in 109,9 s = 25,1 MB/s, but it is not being used to judge that volume: 1 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
7163  2026-09-18 12:05:27.296Z INFO  [f49c4b25cb0b407b85bdeaffe367a040] this walk moved 8167,7 MB of /media/Filmer/The.Silence.of.the.Lambs.1991.edition-Remastered.1080p.BluRay.DDP.5.1.8bit.H.265-iVy/The.Silence.of.the.Lambs.1991.edition-Remastered.1080p.BluRay.DDP.5.1.8bit.H.265-iVy.mkv in 113,1 s = 75,8 MB/s, but it is not being used to judge that volume: 2 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
8057  2026-09-18 12:13:22.784Z INFO  [ad0e637649054684b02254de49364e5a] this walk moved 1907,4 MB of /Media/TV Shows/The Trio/Season 1/The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv in 1,3 s = 1561,3 MB/s, but it is not being used to judge that volume: 3 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
9594  2026-09-18 14:22:34.878Z INFO  [658cf5435ae140358e4e54f014b0aa2b] this walk moved 8483,6 MB of /Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.mkv in 58,7 s = 151,5 MB/s - the ceiling for that volume is none (this volume measured 0,85 ms per read, which is fast)
9604  2026-09-18 14:25:25.369Z INFO  [5b5ffb09489449f68f0cf2ee380b98fc] this walk moved 8483,6 MB of /Media/Movies/False Trail (2011)/False Trail (2011) Bluray-1080p.mkv in 3,4 s = 314,1 MB/s - the ceiling for that volume is none (this volume measured 0,85 ms per read, which is fast)
12098  2026-09-19 00:15:26.907Z INFO  [7e13311e4e214c9a95c1aedcced2e987] this walk moved 1362,5 MB of /Media/Movies/Spermageddon (2024)/Spermageddon (2024) Bluray-1080p.mp4 in 40,1 s = 35,6 MB/s, but it is not being used to judge that volume: 5 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
12222  2026-09-19 00:15:54.993Z INFO  [1170914f61bd4c0b8b1ca549d694db54] this walk moved 2631,6 MB of /media/synology/Syn-Serier/The Unlikely Murderer/Season 1/The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv in 6,3 s = 440,3 MB/s, but it is not being used to judge that volume: 7 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
12262  2026-09-19 00:16:01.926Z INFO  [0873927ae4184fcc868b541d17e07669] this walk moved 2631,6 MB of /media/synology/Syn-Serier/The Unlikely Murderer/Season 1/The Unlikely Murderer - S01E05 - Episode 5 WEBDL-1080p.mkv in 6,5 s = 425,9 MB/s, but it is not being used to judge that volume: 7 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
12739  2026-09-19 00:17:56.403Z INFO  [1bc9cec0d1624148aeef43a1428f05c2] this walk moved 1907,4 MB of /Media/TV Shows/The Trio/Season 1/The Trio - S01E04 - Episode 4 WEBDL-1080p.mkv in 10,2 s = 195,3 MB/s, but it is not being used to judge that volume: 5 job(s) on another volume were being read at the same time, so this number belongs to the moment rather than to the storage
```
