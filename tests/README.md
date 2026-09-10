# Checks

Regression checks for the plugin. They exist because most of this project's behaviour is
invisible: how much of a file the reader touches, whether a worker waits for another worker,
whether the configured worker count is honoured, whether a progress bar tells the truth. None of
that shows up by looking at the interface, and each of those was a real bug that reached a beta
release before a check existed for it.

## Running them

```bash
python3 tests/run_checks.py
```

Requires the **.NET 9 SDK** and **python3**. Nothing else: no ffsubsync binary, no media library,
no Jellyfin instance. Fixtures are synthetic Matroska files built on the fly, so the checks never
touch real media.

`dotnet` is found on `PATH`, or from `DOTNET_ROOT`, `~/.dotnet`, `/usr/share/dotnet`,
`/opt/dotnet`, `/opt/data/.dotnet`; set `DOTNET=/path/to/dotnet` to be explicit. Scratch files
(the generated harness project and fixtures) go to `.tests-work/`, which is git-ignored;
`TESTS_WORK` overrides the location.

## What is covered

**Extraction cost.** How many bytes and read calls the Matroska reader needs for a file with
subtitle cue points, without them, with a cue index but no block offsets, and with no cue index at
all. The indexed path must stay a small fraction of the file, and must stay cheaper than walking
clusters.

**Scheduling.** A job occupies a slot rather than a group: an empty pool fills every slot, one
free slot starts exactly one job, a full pool starts nothing, and a media file already being read
is not given a second worker. Worker counts 1, 3, 16, 32 and 64 are honoured as written — a silent
cap at 8 was once the reason a 32-worker setting ran eight.

**Language matching and configuration.** ISO 639-1/639-2 normalisation, the language filter,
mode selection (auto/normal/parallel/fast/ultimate), and speech-cache keying.

**Progress accounting.** A progress line's `done/total` counters must map to a fraction, so the
bar follows real work; a line without counters, or with a zero total, must yield nothing rather
than a division error.

## Adding a check

Add the assertion to `tests/run_checks.py` (C# checks live in the `PROGRAM` string, Python checks
drive fixtures). Write the check for the *bug*, not for the fix: state what must not regress, so a
later change that reintroduces it fails loudly.

Keep fixtures synthetic. A check that needs a 40 GB remux or a running Jellyfin will be skipped
the moment it is inconvenient, and a skipped check protects nothing.
